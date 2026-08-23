#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class WinDivertInstallerTests
    {
        [TestMethod]
        public void SecurityPinsMatchReviewedWinDivertRelease()
        {
            Assert.AreEqual("2.2.2", ReadWinDivertConstant(nameof(WinDivertInstaller.Version)));
            Assert.AreEqual(
                "https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip",
                ReadWinDivertConstant(nameof(WinDivertInstaller.PackageUrl)));
            Assert.AreEqual(
                "c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2",
                ReadWinDivertConstant(nameof(WinDivertInstaller.DllSha256)));
            Assert.AreEqual(
                "8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2",
                ReadWinDivertConstant(nameof(WinDivertInstaller.DriverSha256)));
        }

        private static string ReadWinDivertConstant(string fieldName)
        {
            System.Reflection.FieldInfo field = typeof(WinDivertInstaller).GetField(
                fieldName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?? throw new AssertFailedException($"Missing public constant '{fieldName}'.");
            return (string)(field.GetRawConstantValue()
                ?? throw new AssertFailedException($"Constant '{fieldName}' has no value."));
        }

        [TestMethod]
        public void ExtractRuntimeAcceptsOnlyExpectedX64Entries()
        {
            string root = CreateTempDirectory();
            try
            {
                byte[] dll = CreatePe(0x8664);
                byte[] driver = CreatePe(0x8664);
                byte[] package = CreatePackage(dll, driver);

                WinDivertInstaller.ExtractRuntime(
                    package,
                    root,
                    Sha256(dll),
                    Sha256(driver));

                Assert.IsTrue(File.Exists(Path.Combine(root, "WinDivert.dll")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "WinDivert64.sys")));
                WinDivertInstaller.ValidateX64PortableExecutable(Path.Combine(root, "WinDivert.dll"));
                WinDivertInstaller.ValidateX64PortableExecutable(Path.Combine(root, "WinDivert64.sys"));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [TestMethod]
        public void ExtractRuntimeRejectsWrongArchitecture()
        {
            string root = CreateTempDirectory();
            try
            {
                byte[] dll = CreatePe(0x014c);
                byte[] driver = CreatePe(0x8664);
                byte[] package = CreatePackage(dll, driver);
                Assert.ThrowsExactly<InvalidDataException>(() => WinDivertInstaller.ExtractRuntime(
                    package,
                    root,
                    Sha256(dll),
                    Sha256(driver)));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [TestMethod]
        public void ExtractRuntimeRejectsUnexpectedArchiveLayout()
        {
            string root = CreateTempDirectory();
            try
            {
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteEntry(archive, "unexpected/x64/WinDivert.dll", CreatePe(0x8664));
                    WriteEntry(archive, "unexpected/x64/WinDivert64.sys", CreatePe(0x8664));
                }

                Assert.ThrowsExactly<InvalidDataException>(() => WinDivertInstaller.ExtractRuntime(
                    buffer.ToArray(),
                    root,
                    new string('0', 64),
                    new string('0', 64)));
            }
            finally
            {
                TryDelete(root);
            }
        }


        [TestMethod]
        public void ExtractRuntimeRejectsPinnedHashMismatch()
        {
            string root = CreateTempDirectory();
            try
            {
                byte[] dll = CreatePe(0x8664);
                byte[] driver = CreatePe(0x8664);
                byte[] package = CreatePackage(dll, driver);

                Assert.ThrowsExactly<InvalidDataException>(() => WinDivertInstaller.ExtractRuntime(
                    package,
                    root,
                    new string('0', 64),
                    Sha256(driver)));
                Assert.IsFalse(File.Exists(Path.Combine(root, "WinDivert.dll")));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [TestMethod]
        public void ValidatePinnedRuntimeFileRejectsTamperedCache()
        {
            string root = CreateTempDirectory();
            try
            {
                string path = Path.Combine(root, "WinDivert.dll");
                byte[] image = CreatePe(0x8664);
                File.WriteAllBytes(path, image);
                string expected = Sha256(image);

                WinDivertInstaller.ValidatePinnedRuntimeFile(path, expected);
                image[^1] ^= 0x5A;
                File.WriteAllBytes(path, image);

                Assert.ThrowsExactly<InvalidDataException>(() =>
                    WinDivertInstaller.ValidatePinnedRuntimeFile(path, expected));
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static byte[] CreatePackage(byte[] dll, byte[] driver)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                const string root = "WinDivert-2.2.2-A/x64/";
                WriteEntry(archive, root + "WinDivert.dll", dll);
                WriteEntry(archive, root + "WinDivert64.sys", driver);
            }
            return buffer.ToArray();
        }

        private static void WriteEntry(ZipArchive archive, string path, byte[] content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Fastest);
            using Stream output = entry.Open();
            output.Write(content, 0, content.Length);
        }

        private static byte[] CreatePe(ushort machine)
        {
            byte[] image = new byte[128];
            image[0] = (byte)'M';
            image[1] = (byte)'Z';
            BitConverter.GetBytes(0x40).CopyTo(image, 0x3C);
            image[0x40] = (byte)'P';
            image[0x41] = (byte)'E';
            BitConverter.GetBytes(machine).CopyTo(image, 0x44);
            return image;
        }

        private static string Sha256(byte[] content)
        {
            return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        }

        private static string CreateTempDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "Shadowsocks.UnitTests", "WinDivert", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void TryDelete(string path)
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
    }
}
