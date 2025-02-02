// Copyright (C) 2021 jmh
// SPDX-License-Identifier: GPL-3.0-only

using AuthenticatorPro.Shared.Entity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCLCrypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AuthenticatorPro.Shared.Data.Backup
{
    public class Backup
    {
        public const string FileExtension = "authpro";
        public const string MimeType = "application/octet-stream";

        // PCLCrypto does not support anything other than SHA1 on Android unfortunately
        private const KeyDerivationAlgorithm KeyDerivationAlgorithm = PCLCrypto.KeyDerivationAlgorithm.Pbkdf2Sha1;
        private const SymmetricAlgorithm KeyAlgorithm = SymmetricAlgorithm.AesCbcPkcs7;
        private const string Header = "AuthenticatorPro";
        private const int Iterations = 64000;
        private const int KeySize = 32;
        private const int SaltLength = 20;

        public IEnumerable<Authenticator> Authenticators { get; }
        public IEnumerable<Category> Categories { get; }
        public IEnumerable<AuthenticatorCategory> AuthenticatorCategories { get; }
        public IEnumerable<CustomIcon> CustomIcons { get; }

        public Backup(IEnumerable<Authenticator> authenticators, IEnumerable<Category> categories = null,
            IEnumerable<AuthenticatorCategory> authenticatorCategories = null,
            IEnumerable<CustomIcon> customIcons = null)
        {
            Authenticators = authenticators ??
                             throw new ArgumentNullException(nameof(authenticators),
                                 "Backup must contain authenticators");
            Categories = categories;
            AuthenticatorCategories = authenticatorCategories;
            CustomIcons = customIcons;
        }

        private static Tuple<ICryptographicKey, int> GetKeyAndBlockLength(byte[] salt, string password)
        {
            var keyDerivationProvider =
                WinRTCrypto.KeyDerivationAlgorithmProvider.OpenAlgorithm(KeyDerivationAlgorithm);
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            var initialKey = keyDerivationProvider.CreateKey(passwordBytes);

            var parameters = WinRTCrypto.KeyDerivationParameters.BuildForPbkdf2(salt, Iterations);
            var material = WinRTCrypto.CryptographicEngine.DeriveKeyMaterial(initialKey, parameters, KeySize);

            var provider = WinRTCrypto.SymmetricKeyAlgorithmProvider.OpenAlgorithm(KeyAlgorithm);
            return new Tuple<ICryptographicKey, int>(provider.CreateSymmetricKey(material), provider.BlockLength);
        }

        public byte[] ToBytes(string password)
        {
            var json = JsonConvert.SerializeObject(this);

            if (String.IsNullOrEmpty(password))
            {
                return Encoding.UTF8.GetBytes(json);
            }

            var salt = WinRTCrypto.CryptographicBuffer.GenerateRandom(SaltLength);
            var (key, blockLength) = GetKeyAndBlockLength(salt, password);
            var iv = WinRTCrypto.CryptographicBuffer.GenerateRandom(blockLength);

            var unencryptedData = Encoding.UTF8.GetBytes(json);
            var encryptedData = WinRTCrypto.CryptographicEngine.Encrypt(key, unencryptedData, iv);

            var headerBytes = Encoding.UTF8.GetBytes(Header);
            var output = new byte[Header.Length + SaltLength + blockLength + encryptedData.Length];

            Buffer.BlockCopy(headerBytes, 0, output, 0, headerBytes.Length);
            Buffer.BlockCopy(salt, 0, output, headerBytes.Length, SaltLength);
            Buffer.BlockCopy(iv, 0, output, headerBytes.Length + SaltLength, blockLength);
            Buffer.BlockCopy(encryptedData, 0, output, headerBytes.Length + SaltLength + blockLength,
                encryptedData.Length);

            return output;
        }

        public static Backup FromBytes(byte[] data, string password)
        {
            string json;

            if (String.IsNullOrEmpty(password))
            {
                json = Encoding.UTF8.GetString(data);
            }
            else
            {
                var foundHeader = data.Take(Header.Length).ToArray();
                var headerBytes = Encoding.UTF8.GetBytes(Header);

                if (!headerBytes.SequenceEqual(foundHeader))
                {
                    throw new ArgumentException("Header does not match.");
                }

                var salt = data.Skip(Header.Length).Take(SaltLength).ToArray();
                var (key, blockLength) = GetKeyAndBlockLength(salt, password);
                var iv = data.Skip(Header.Length).Skip(SaltLength).Take(blockLength).ToArray();
                var payload = data.Skip(Header.Length + SaltLength + blockLength)
                    .Take(data.Length - Header.Length - SaltLength - blockLength).ToArray();

                var raw = WinRTCrypto.CryptographicEngine.Decrypt(key, payload, iv);
                json = Encoding.UTF8.GetString(raw);
            }

            try
            {
                return JsonConvert.DeserializeObject<Backup>(json);
            }
            catch (JsonException e)
            {
                throw new ArgumentException("File invalid", e);
            }
        }

        public static bool IsReadableWithoutPassword(byte[] data)
        {
            if (data[0] != '{' || data[^1] != '}')
            {
                return false;
            }

            try
            {
                var json = Encoding.UTF8.GetString(data);
                _ = JObject.Parse(json);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}