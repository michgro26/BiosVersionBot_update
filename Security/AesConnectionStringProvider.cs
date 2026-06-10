using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BiosVersionBot.Security
{
    public sealed class AesConnectionStringProvider : IConnectionStringProvider
    {
        private readonly string _secureFilePath;
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("9E1C7A4B6F2D9G8H");
        private static readonly byte[] Iv = Encoding.UTF8.GetBytes("A7C5E9B1D3F2H4J6");

        public AesConnectionStringProvider(string secureFilePath)
        {
            _secureFilePath = secureFilePath;
        }

        public string GetConnectionString()
        {
            if (!File.Exists(_secureFilePath))
                throw new FileNotFoundException($"Nie znaleziono pliku secureconn.dat: {_secureFilePath}");

            string cipherText = File.ReadAllText(_secureFilePath).Trim();
            if (string.IsNullOrWhiteSpace(cipherText))
                throw new InvalidOperationException($"Plik secureconn.dat jest pusty: {_secureFilePath}");

            return Decrypt(cipherText);
        }

        private static string Decrypt(string cipherText)
        {
            byte[] buffer = Convert.FromBase64String(cipherText);

            using Aes aes = Aes.Create();
            aes.Key = Key;
            aes.IV = Iv;

            using MemoryStream memoryStream = new(buffer);
            using ICryptoTransform decryptor = aes.CreateDecryptor();
            using CryptoStream cryptoStream = new(memoryStream, decryptor, CryptoStreamMode.Read);
            using StreamReader streamReader = new(cryptoStream);

            return streamReader.ReadToEnd();
        }
    }
}
