﻿// Copyright (c) Lex Li. All rights reserved.
// 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using Microsoft.Web.Administration;
using RollbarDotNet;

namespace JexusManager.Features.Certificates
{
    using System;
    using System.Collections;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Windows.Forms;

    using Microsoft.Web.Management.Client.Win32;

    using Mono.Security.X509;
    using Mono.Security.X509.Extensions;

    using Org.BouncyCastle.Crypto;
    using Org.BouncyCastle.Crypto.Digests;
    using Org.BouncyCastle.Security;
    using Org.BouncyCastle.X509;

    using X509Certificate = Mono.Security.X509.X509Certificate;

    internal partial class SelfCertificateDialog : DialogForm
    {
        public SelfCertificateDialog(IServiceProvider serviceProvider, CertificatesFeature feature)
            : base(serviceProvider)
        {
            InitializeComponent();
            cbStore.SelectedIndex = 0;
            cbLength.SelectedIndex = 3;
            cbHashing.SelectedIndex = 1;
            txtCommonName.Text = Environment.MachineName;
            dtpFrom.Value = DateTime.Now;
            dtpTo.Value = dtpFrom.Value.AddYears(1);

            if (Environment.OSVersion.Version < Version.Parse("6.2"))
            {
                // IMPORTANT: WebHosting store is available since Windows 8.
                cbStore.Enabled = false;
            }

            if (!Helper.IsRunningOnMono())
            {
                NativeMethods.TryAddShieldToButton(btnOK);
            }

            var container = new CompositeDisposable();
            FormClosed += (sender, args) => container.Dispose();

            container.Add(
                Observable.FromEventPattern<EventArgs>(txtName, "TextChanged")
                .ObserveOn(System.Threading.SynchronizationContext.Current)
                .Subscribe(evt =>
                {
                    btnOK.Enabled = !string.IsNullOrWhiteSpace(txtName.Text);
                }));

            container.Add(
                Observable.FromEventPattern<EventArgs>(btnOK, "Click")
                .ObserveOn(System.Threading.SynchronizationContext.Current)
                .Subscribe(evt =>
                {
                    var names = txtCommonName.Text;
                    if (string.IsNullOrWhiteSpace(names))
                    {
                        ShowMessage("DNS names cannot be empty.", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                        return;
                    }

                    var dnsNames = names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).ToArray();
                    if (dnsNames.Length == 0)
                    {
                        ShowMessage("DNS names cannot be empty.", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                        return;
                    }

                    // Generate certificate
                    string defaultIssuer = string.Format("CN={0}", dnsNames[0]);
                    string defaultSubject = defaultIssuer;
                    byte[] sn = Guid.NewGuid().ToByteArray();
                    string subject = defaultSubject;
                    string issuer = defaultIssuer;
                    DateTime notBefore = dtpFrom.Value;
                    DateTime notAfter = dtpTo.Value;

                    RSA issuerKey = new RSACryptoServiceProvider(int.Parse(cbLength.Text));
                    RSA subjectKey = null;

                    CspParameters subjectParams = new CspParameters();
                    CspParameters issuerParams = new CspParameters();
                    BasicConstraintsExtension bce = new BasicConstraintsExtension
                    {
                        PathLenConstraint = BasicConstraintsExtension.NoPathLengthConstraint,
                        CertificateAuthority = true
                    };
                    ExtendedKeyUsageExtension eku = new ExtendedKeyUsageExtension();
                    eku.KeyPurpose.Add("1.3.6.1.5.5.7.3.1");
                    SubjectAltNameExtension alt = null;
                    string p12File = Path.GetTempFileName();
                    string p12pwd = "test";

                    // serial number MUST be positive
                    if ((sn[0] & 0x80) == 0x80)
                    {
                        sn[0] -= 0x80;
                    }

                    if (subject != defaultSubject)
                    {
                        issuer = subject;
                        issuerKey = null;
                    }
                    else
                    {
                        subject = issuer;
                        subjectKey = issuerKey;
                    }

                    if (subject == null)
                        throw new Exception("Missing Subject Name");

                    X509CertificateBuilder cb = new X509CertificateBuilder(3);
                    cb.SerialNumber = sn;
                    cb.IssuerName = issuer;
                    cb.NotBefore = notBefore;
                    cb.NotAfter = notAfter;
                    cb.SubjectName = subject;
                    cb.SubjectPublicKey = subjectKey;
                    // extensions
                    if (bce != null)
                        cb.Extensions.Add(bce);
                    if (eku != null)
                        cb.Extensions.Add(eku);
                    if (alt != null)
                        cb.Extensions.Add(alt);

                    IDigest digest = new Sha1Digest();
                    byte[] resBuf = new byte[digest.GetDigestSize()];
                    var spki = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(DotNetUtilities.GetRsaPublicKey(issuerKey));
                    byte[] bytes = spki.PublicKeyData.GetBytes();
                    digest.BlockUpdate(bytes, 0, bytes.Length);
                    digest.DoFinal(resBuf, 0);

                    cb.Extensions.Add(new SubjectKeyIdentifierExtension { Identifier = resBuf });
                    cb.Extensions.Add(new AuthorityKeyIdentifierExtension { Identifier = resBuf });
                    if (cbGenerate.Checked)
                    {
                        SubjectAltNameExtension subjectAltNameExtension = new SubjectAltNameExtension(
                            new string[0],
                            dnsNames,
                            new string[0],
                            new string[0])
                        { Critical = false };
                        cb.Extensions.Add(subjectAltNameExtension);
                    }

                    // signature
                    string hashName = cbHashing.SelectedIndex == 0 ? "SHA1" : "SHA256";
                    cb.Hash = hashName;
                    byte[] rawcert = null;
                    try
                    {
                        rawcert = cb.Sign(issuerKey);
                    }
                    catch (Exception ex)
                    {
                        Rollbar.Report(ex, ErrorLevel.Error);
                        ShowError(ex, "Certificate generation error", false);
                        return;
                    }

                    PKCS12 p12 = new PKCS12();
                    p12.Password = p12pwd;

                    ArrayList list = new ArrayList();
                    // we use a fixed array to avoid endianess issues 
                    // (in case some tools requires the ID to be 1).
                    list.Add(new byte[] { 1, 0, 0, 0 });
                    Hashtable attributes = new Hashtable(1);
                    attributes.Add(PKCS9.localKeyId, list);

                    p12.AddCertificate(new X509Certificate(rawcert), attributes);
                    p12.AddPkcs8ShroudedKeyBag(subjectKey, attributes);
                    p12.SaveToFile(p12File);

                    Item = new X509Certificate2(p12File, p12pwd) { FriendlyName = txtName.Text };
                    Store = cbStore.SelectedIndex == 0 ? "Personal" : "WebHosting";

                    try
                    {
                        using (var process = new Process())
                        {
                            // add certificate
                            var start = process.StartInfo;
                            start.Verb = "runas";
                            start.FileName = "cmd";
                            start.Arguments = string.Format("/c \"\"{4}\" /f:\"{0}\" /p:{1} /n:\"{2}\" /s:{3}\"",
                                p12File,
                                p12pwd,
                                txtName.Text,
                                cbStore.SelectedIndex == 0 ? "MY" : "WebHosting",
                                Path.Combine(Environment.CurrentDirectory, "certificateinstaller.exe"));
                            start.CreateNoWindow = true;
                            start.WindowStyle = ProcessWindowStyle.Hidden;
                            process.Start();
                            process.WaitForExit();
                            File.Delete(p12File);
                            if (process.ExitCode == 0)
                            {
                                DialogResult = DialogResult.OK;
                            }
                            else
                            {
                                ShowMessage(process.ExitCode.ToString(), MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
                            }
                        }
                    }
                    catch (Win32Exception ex)
                    {
                        // elevation is cancelled.
                        if (ex.NativeErrorCode != Microsoft.Web.Administration.NativeMethods.ErrorCancelled)
                        {
                            Rollbar.Report(ex, ErrorLevel.Error, new Dictionary<string, object> {{ "native", ex.NativeErrorCode } });
                            // throw;
                        }
                    }
                    catch (Exception ex)
                    {
                        Rollbar.Report(ex, ErrorLevel.Error);
                    }
                }));

            container.Add(
                Observable.FromEventPattern<CancelEventArgs>(this, "HelpButtonClicked")
                .ObserveOn(System.Threading.SynchronizationContext.Current)
                .Subscribe(EnvironmentVariableTarget =>
                {
                    feature.ShowHelp();
                }));
        }

        public string Store { get; set; }

        public X509Certificate2 Item { get; set; }
    }
}
