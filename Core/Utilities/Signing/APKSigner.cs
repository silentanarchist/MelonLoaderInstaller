using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Store;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.Cms;
using System.IO;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO.Compression;

namespace MelonLoader.Installer.Core.Utilities.Signing
{
    public class APKSigner
    {
        private X509Certificate _xCert;
        private AsymmetricKeyParameter _privateKey;
        private readonly SHA256 _sha;

        private readonly UTF8Encoding _encoding;
        private readonly IPatchLogger _logger;

        public APKSigner(string pemData, IPatchLogger patchLogger)
        {
            _encoding = new UTF8Encoding(false);
            _sha = SHA256.Create();
            _logger = patchLogger;

            LoadCerts(pemData);
        }

        private void LoadCerts(string pemData)
        {
            _logger.Log("Reading certificates");

            using (var reader = new StringReader(pemData))
            {
                // Iterate through the PEM objects until we find the public or private key
                var pemReader = new PemReader(reader);
                object pemObject;
                while ((pemObject = pemReader.ReadObject()) != null)
                {
                    _xCert ??= pemObject as X509Certificate;
                    _privateKey ??= (pemObject as AsymmetricCipherKeyPair)?.Private;
                }
            }

            if (_xCert == null)
                throw new System.Security.SecurityException("Certificate could not be loaded from PEM data.");

            if (_privateKey == null)
                throw new System.Security.SecurityException("Private Key could not be loaded from PEM data.");
        }

        public void Sign(string apkPath)
        {
            /* This isn't necessary and should make it so on Quest it won't keep screaming at the user that they should restore the app, until they patch it at least.
            
            _logger.Log("Signing with V1, this can take a few");
            SignV1(apkPath);*/

            _logger.Log("Aligning");
            APKAligner.AlignApk(apkPath);

            _logger.Log("Signing with V2");
            SignV2(apkPath);

            _logger.Log("Done");
        }

        #region V1

        private void SignV1(string apkPath)
        {
            using FileStream zipStream = new(apkPath, FileMode.Open);
            using ZipArchive archive = new(zipStream, ZipArchiveMode.Read | ZipArchiveMode.Update);

            #region Create MANIFEST.MF

            using MemoryStream manifestStream = new();
            using StreamWriter manifestWriter = AsStreamWriter(manifestStream);

            using MemoryStream sigHolderStream = new();
            using StreamWriter sigHolderWriter = AsStreamWriter(sigHolderStream);

            manifestWriter.WriteLine("Manifest-Version: 1.0");
            manifestWriter.WriteLine("Created-By: LemonLoader");
            manifestWriter.WriteLine();

            manifestWriter.Close();

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("META-INF"))
                    continue;

                WriteDigests(entry, manifestStream, sigHolderWriter);
            }

            sigHolderWriter.Close();

            #endregion

            #region Create LEMON.SF

            using MemoryStream sigStream = new();
            using StreamWriter sigWriter = AsStreamWriter(sigStream);

            manifestStream.Seek(0, SeekOrigin.Begin);
            byte[] manifestSha = _sha.ComputeHash(manifestStream);

            sigWriter.WriteLine("Signature-Version: 1.0");
            sigWriter.WriteLine("Created-By: LemonLoader");
            sigWriter.WriteLine($"SHA-256-Digest-Manifest: {Convert.ToBase64String(manifestSha)}");
            sigWriter.WriteLine();

            sigWriter.Close();

            sigHolderStream.Seek(0, SeekOrigin.Begin);
            sigHolderStream.CopyTo(sigStream);

            #endregion

            #region Add to APK

            // Remove old META-INF files
            foreach (ZipArchiveEntry entry in archive.Entries.Where(a => a.FullName.StartsWith("META-INF")))
                entry.Delete();

            // Add the new
            manifestStream.Seek(0, SeekOrigin.Begin);
            sigStream.Seek(0, SeekOrigin.Begin);

            ZipArchiveEntry manifestEntry = archive.CreateEntry("META-INF/MANIFEST.MF");
            ZipArchiveEntry sigEntry = archive.CreateEntry("META-INF/LEMON.SF");
            ZipArchiveEntry rsaEntry = archive.CreateEntry("META-INF/LEMON.RSA");

            using Stream manifestEntryStream = manifestEntry.Open();
            manifestStream.CopyTo(manifestEntryStream);

            using Stream sigEntryStream = sigEntry.Open();
            sigStream.CopyTo(sigEntryStream);

            sigStream.Seek(0, SeekOrigin.Begin);

            byte[] signedSig = GetSignatureFileSig(sigStream);
            using Stream rsaEntryStream = rsaEntry.Open();
            rsaEntryStream.Write(signedSig);

            #endregion
        }

        private void WriteDigests(ZipArchiveEntry entry, Stream manifestStream, StreamWriter sigHolderWriter)
        {
            using Stream entryStream = entry.Open();
            string entryDigest = Convert.ToBase64String(_sha.ComputeHash(entryStream));

            using MemoryStream proxyStream = new();
            using StreamWriter proxyWriter = AsStreamWriter(proxyStream);

            proxyWriter.WriteLine($"Name: {entry.FullName}");
            proxyWriter.WriteLine($"SHA-256-Digest: {entryDigest}");
            proxyWriter.WriteLine();

            proxyWriter.Close();

            proxyStream.Seek(0, SeekOrigin.Begin);

            sigHolderWriter.WriteLine($"Name: {entry.FullName}");
            sigHolderWriter.WriteLine($"SHA-256-Digest: {Convert.ToBase64String(_sha.ComputeHash(proxyStream))}");
            sigHolderWriter.WriteLine();
            proxyStream.Seek(0, SeekOrigin.Begin);
            proxyStream.CopyTo(manifestStream);
        }

        private byte[] GetSignatureFileSig(Stream sfStream)
        {
            var certStore = X509StoreFactory.Create("Certificate/Collection", new X509CollectionStoreParameters(new List<X509Certificate> { _xCert }));
            CmsSignedDataGenerator dataGen = new();
            dataGen.AddCertificates(certStore);
            dataGen.AddSigner(_privateKey, _xCert, CmsSignedGenerator.EncryptionRsa, CmsSignedGenerator.DigestSha256);

            // Content is detached - i.e. not included in the signature block itself
            CmsProcessableInputStream detachedContent = new(sfStream);
            var signedContent = dataGen.Generate(detachedContent, false);

            // Get the signature in the proper ASN.1 structure for java to parse it properly.  Lots of trial and error
            var signerInfos = signedContent.GetSignerInfos();
            var signer = signerInfos.GetSigners().Cast<SignerInformation>().First();
            SignerInfo signerInfo = signer.ToSignerInfo();
            Asn1EncodableVector digestAlgorithmsVector =
            [
                new AlgorithmIdentifier(new DerObjectIdentifier("2.16.840.1.101.3.4.2.1"), DerNull.Instance)
            ];
            ContentInfo encapContentInfo = new(new DerObjectIdentifier("1.2.840.113549.1.7.1"), null);
            Asn1EncodableVector asnVector =
            [
                X509CertificateStructure.GetInstance(Asn1Object.FromByteArray(_xCert.GetEncoded()))
            ];
            Asn1EncodableVector signersVector = [signerInfo.ToAsn1Object()];
            SignedData signedData = new(new DerSet(digestAlgorithmsVector), encapContentInfo, new BerSet(asnVector), null, new DerSet(signersVector));
            ContentInfo contentInfo = new(new DerObjectIdentifier("1.2.840.113549.1.7.2"), signedData);
            return contentInfo.GetDerEncoded();
        }

        private StreamWriter AsStreamWriter(MemoryStream ms) => new(ms, _encoding, 1024, true);

        #endregion

        #region V2

        // Warpforge Revival change: the signing block is written into the APK in place. Everything before the
        // central directory stays where it is, so only the central directory and its end record (a few hundred KB
        // at most) are held in memory, then the block, the central directory and the end record are written from
        // the old central directory's offset onward. The original copied the whole APK into a MemoryStream and back,
        // over a gigabyte for the game's data pack: too much for the patcher app on a phone. Same bytes as before.
        private void SignV2(string path)
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.ReadWrite);
            FileMemory memory = new(fs);
            memory.Position = memory.Length() - 22;
            while (memory.ReadInt() != EndOfCentralDirectory.SIGNATURE)
            {
                memory.Position -= 4 + 1;
            }
            memory.Position -= 4;
            var eocdPosition = memory.Position;
            EndOfCentralDirectory eocd = new(memory);
            var cd = eocd.OffsetOfCD;
            var section1 = GetSectionDigests(fs, 0, cd);
            var section3 = GetSectionDigests(fs, cd, eocdPosition);
            var section4 = GetSectionDigests(fs, eocdPosition, fs.Length);

            var digestChunks = section1.Concat(section3).Concat(section4).ToList();

            byte[] bytes = new byte[1 + 4];
            bytes[0] = 0x5a;
            byte[] sizeBytes = BitConverter.GetBytes((uint)digestChunks.Count);
            bytes[1] = sizeBytes[0];
            bytes[2] = sizeBytes[1];
            bytes[3] = sizeBytes[2];
            bytes[4] = sizeBytes[3];
            byte[] digestInput = new byte[bytes.Length + digestChunks.Sum(c => c.Length)];
            Buffer.BlockCopy(bytes, 0, digestInput, 0, bytes.Length);
            int at = bytes.Length;
            foreach (byte[] chunk in digestChunks)
            {
                Buffer.BlockCopy(chunk, 0, digestInput, at, chunk.Length);
                at += chunk.Length;
            }
            var digest = _sha.ComputeHash(digestInput);

            uint algorithm = 0x0103;

            APKSignatureSchemeV2 block = new();
            APKSignatureSchemeV2.Signer signer = new();

            using MemoryStream signedDataMs = new();
            using FileMemory memorySignedData = new(signedDataMs);
            var signedData = new APKSignatureSchemeV2.Signer.BlockSignedData();
            signedData.Digests.Add(new APKSignatureSchemeV2.Signer.BlockSignedData.Digest(algorithm, digest));

            signedData.Certificates.Add(_xCert.GetEncoded());

            signedData.Write(memorySignedData);
            signer.SignedData = signedDataMs.ToArray();

            ISigner signerType = SignerUtilities.GetSigner("SHA256WithRSA");
            signerType.Init(true, _privateKey);
            signerType.BlockUpdate(signer.SignedData, 0, signer.SignedData.Length);

            signer.Signatures.Add(new APKSignatureSchemeV2.Signer.BlockSignature(algorithm, signerType.GenerateSignature()));
            signer.PublicKey = _xCert.CertificateStructure.SubjectPublicKeyInfo.GetDerEncoded();
            block.Signers.Add(signer);

            APKSigningBlock signingBlock = new();
            signingBlock.Values.Add(block.ToIDValuePair());

            // the central directory as it is now (it moves up by the size of the signing block)
            fs.Position = cd;
            byte[] centralDirectory = new byte[eocdPosition - cd];
            fs.ReadExactly(centralDirectory);

            using MemoryStream tail = new();
            using FileMemory tailMemory = new(tail);
            signingBlock.Write(tailMemory);
            eocd.OffsetOfCD = (int)(cd + tail.Position);
            tailMemory.WriteBytes(centralDirectory);
            eocd.Write(tailMemory);

            fs.Position = cd;
            tail.Position = 0;
            tail.CopyTo(fs);
            fs.SetLength(fs.Position);
            fs.Flush(true);
        }

        private List<byte[]> GetSectionDigests(FileStream fs, long startOffset, long endOffset)
        {
            var digests = new List<byte[]>();
            int chunkSize = 1024 * 1024;
            byte[] bytes = new byte[1 + 4 + chunkSize];     // one buffer for every chunk (was a new 1 MB array per chunk)
            for (long i = startOffset; i < endOffset; i += chunkSize)
            {
                fs.Position = i;
                var size = (int)Math.Min(endOffset - i, chunkSize);
                bytes[0] = 0xa5;
                byte[] sizeBytes = BitConverter.GetBytes((uint)size);
                bytes[1] = sizeBytes[0];
                bytes[2] = sizeBytes[1];
                bytes[3] = sizeBytes[2];
                bytes[4] = sizeBytes[3];
                fs.ReadExactly(bytes, 5, size);
                digests.Add(_sha.ComputeHash(bytes, 0, 5 + size));
            }
            return digests;
        }

        #endregion
    }
}
