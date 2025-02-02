// Copyright (C) 2021 jmh
// SPDX-License-Identifier: GPL-3.0-only

using AuthenticatorPro.Shared.Data;
using AuthenticatorPro.Shared.Data.Generator;
using AuthenticatorPro.Shared.Util;
using SimpleBase;
using SQLite;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AuthenticatorPro.Shared.Entity
{
    [Table("authenticator")]
    public class Authenticator
    {
        public const int IssuerMaxLength = 32;
        public const int UsernameMaxLength = 40;

        public const HashAlgorithm DefaultAlgorithm = HashAlgorithm.Sha1;

        [Column("type")] public AuthenticatorType Type { get; set; }

        [Column("icon")] public string Icon { get; set; }

        [Column("issuer")]
        [MaxLength(IssuerMaxLength)]
        public string Issuer { get; set; }

        [Column("username")]
        [MaxLength(UsernameMaxLength)]
        public string Username { get; set; }

        [Column("secret")] [PrimaryKey] public string Secret { get; set; }

        [Column("algorithm")] public HashAlgorithm Algorithm { get; set; }

        [Column("digits")] public int Digits { get; set; }

        [Column("period")] public int Period { get; set; }

        [Column("counter")] public long Counter { get; set; }

        [Column("ranking")] public int Ranking { get; set; }


        private IGenerator _generator;
        private long _lastCounter;
        private string _code;


        public Authenticator()
        {
            _code = null;
            _generator = null;
            _lastCounter = 0;

            Algorithm = DefaultAlgorithm;
            Type = AuthenticatorType.Totp;
            Digits = Type.GetDefaultDigits();
            Period = Type.GetDefaultPeriod();
        }

        public string GetCode(long counter)
        {
            _generator ??= Type switch
            {
                AuthenticatorType.Totp => new Totp(Secret, Period, Algorithm, Digits),
                AuthenticatorType.Hotp => new Hotp(Secret, Algorithm, Digits),
                AuthenticatorType.MobileOtp => new MobileOtp(Secret, Digits),
                AuthenticatorType.SteamOtp => new SteamOtp(Secret),
                _ => throw new ArgumentException("Unknown authenticator type.")
            };

            switch (Type.GetGenerationMethod())
            {
                case GenerationMethod.Time:
                    _code = _generator.Compute(counter);
                    break;

                case GenerationMethod.Counter when _lastCounter == Counter:
                    return _code;

                case GenerationMethod.Counter:
                {
                    _code = _generator.Compute(Counter);
                    _lastCounter = Counter;
                    break;
                }
            }

            return _code;
        }

        public string GetCode()
        {
            long counter;

            switch (Type.GetGenerationMethod())
            {
                case GenerationMethod.Time:
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    counter = now - (now % Period);
                    break;
                }

                case GenerationMethod.Counter:
                    counter = Counter;
                    break;

                default:
                    throw new ArgumentException("Unknown generation method");
            }

            return GetCode(counter);
        }

        public static Authenticator FromOtpAuthMigrationAuthenticator(OtpAuthMigration.Authenticator input,
            IIconResolver iconResolver)
        {
            string issuer;
            string username;

            // Google Auth may not have an issuer, just use the username instead
            if (String.IsNullOrEmpty(input.Issuer))
            {
                issuer = input.Username.Trim().Truncate(IssuerMaxLength);
                username = null;
            }
            else
            {
                issuer = input.Issuer.Trim().Truncate(IssuerMaxLength);
                // For some odd reason the username field always follows a '[issuer]: [username]' format
                username = input.Username.Replace($"{input.Issuer}: ", "").Trim().Truncate(UsernameMaxLength);
            }

            var type = input.Type switch
            {
                OtpAuthMigration.Type.Totp => AuthenticatorType.Totp,
                OtpAuthMigration.Type.Hotp => AuthenticatorType.Hotp,
                _ => throw new ArgumentOutOfRangeException(nameof(input.Type), "Unknown type")
            };

            var algorithm = input.Algorithm switch
            {
                OtpAuthMigration.Algorithm.Sha1 => HashAlgorithm.Sha1,
                _ => throw new ArgumentOutOfRangeException(nameof(input.Algorithm), "Unknown algorithm")
            };

            string secret;

            try
            {
                secret = Base32.Rfc4648.Encode(input.Secret);
                secret = CleanSecret(secret, type);
            }
            catch (Exception e)
            {
                throw new ArgumentException("Failed to parse secret", e);
            }

            var auth = new Authenticator
            {
                Issuer = issuer,
                Username = username,
                Algorithm = algorithm,
                Type = type,
                Secret = secret,
                Counter = input.Counter,
                Digits = type.GetDefaultDigits(),
                Period = type.GetDefaultPeriod(),
                Icon = iconResolver.FindServiceKeyByName(issuer)
            };

            if (!auth.IsValid())
            {
                throw new ArgumentException("Authenticator is invalid");
            }

            return auth;
        }

        public static Authenticator FromOtpAuthUri(string uri, IIconResolver iconResolver)
        {
            var uriMatch = Regex.Match(Uri.UnescapeDataString(uri), @"^otpauth:\/\/([a-z]+)\/([^?]*)(.*)$");

            if (!uriMatch.Success)
            {
                throw new ArgumentException("URI is not valid");
            }

            // Get the issuer and username if possible
            var issuerUsername = uriMatch.Groups[2].Value;
            var issuerUsernameMatch = Regex.Match(issuerUsername, @"^(.*?):(.*)$");

            var queryString = uriMatch.Groups[3].Value;

            var argMatches = Regex.Matches(queryString, "([^?=&]+)(=([^&]*))?");
            var args = new Dictionary<string, string>();

            foreach (Match match in argMatches)
            {
                if (!args.ContainsKey(match.Groups[1].Value))
                {
                    args.Add(match.Groups[1].Value, match.Groups[3].Value);
                }
            }

            string issuer;
            string username;

            if (issuerUsernameMatch.Success)
            {
                var issuerValue = issuerUsernameMatch.Groups[1].Value;
                var usernameValue = issuerUsernameMatch.Groups[2].Value;

                if (issuerValue == "")
                {
                    issuer = usernameValue;
                    username = null;
                }
                else
                {
                    issuer = issuerValue;
                    username = usernameValue;
                }
            }
            else
            {
                if (args.ContainsKey("issuer"))
                {
                    issuer = args["issuer"];
                    username = issuerUsername;
                }
                else
                {
                    issuer = uriMatch.Groups[2].Value;
                    username = null;
                }
            }

            var type = uriMatch.Groups[1].Value switch
            {
                "totp" when issuer == "Steam" || args.ContainsKey("steam") => AuthenticatorType.SteamOtp,
                "totp" => AuthenticatorType.Totp,
                "hotp" => AuthenticatorType.Hotp,
                _ => throw new ArgumentException("Unknown type")
            };

            var algorithm = DefaultAlgorithm;

            if (args.ContainsKey("algorithm") && type != AuthenticatorType.SteamOtp)
            {
                algorithm = args["algorithm"].ToUpper() switch
                {
                    "SHA1" => HashAlgorithm.Sha1,
                    "SHA256" => HashAlgorithm.Sha256,
                    "SHA512" => HashAlgorithm.Sha512,
                    _ => throw new ArgumentException("Unknown algorithm")
                };
            }

            var digits = type.GetDefaultDigits();
            if (args.ContainsKey("digits") && !Int32.TryParse(args["digits"], out digits))
            {
                throw new ArgumentException("Digits parameter cannot be parsed.");
            }

            var period = type.GetDefaultPeriod();
            if (args.ContainsKey("period") && !Int32.TryParse(args["period"], out period))
            {
                throw new ArgumentException("Period parameter cannot be parsed.");
            }

            var counter = 0;
            if (type == AuthenticatorType.Hotp && args.ContainsKey("counter") &&
                !Int32.TryParse(args["counter"], out counter))
            {
                throw new ArgumentException("Counter parameter cannot be parsed.");
            }

            if (counter < 0)
            {
                throw new ArgumentException("Counter cannot be negative.");
            }

            if (!args.ContainsKey("secret"))
            {
                throw new ArgumentException("Secret parameter is required.");
            }

            var icon = iconResolver.FindServiceKeyByName(args.ContainsKey("icon") ? args["icon"] : issuer);
            var secret = CleanSecret(args["secret"], type);

            var auth = new Authenticator
            {
                Secret = secret,
                Issuer = issuer.Trim().Truncate(IssuerMaxLength),
                Username = username?.Trim().Truncate(UsernameMaxLength),
                Icon = icon,
                Type = type,
                Algorithm = algorithm,
                Digits = digits,
                Period = period,
                Counter = counter
            };

            if (!auth.IsValid())
            {
                throw new ArgumentException("Authenticator is invalid");
            }

            return auth;
        }

        public string GetOtpAuthUri()
        {
            var type = Type switch
            {
                AuthenticatorType.Hotp => "hotp",
                AuthenticatorType.Totp => "totp",
                AuthenticatorType.SteamOtp => "totp",
                _ => throw new NotSupportedException("Unsupported authenticator type.")
            };

            var issuerUsername = String.IsNullOrEmpty(Username) ? Issuer : $"{Issuer}:{Username}";

            var uri = new StringBuilder(
                $"otpauth://{type}/{Uri.EscapeDataString(issuerUsername)}?secret={Secret}&issuer={Uri.EscapeDataString(Issuer)}");

            if (Algorithm != DefaultAlgorithm)
            {
                var algorithmName = Algorithm switch
                {
                    HashAlgorithm.Sha1 => "SHA1",
                    HashAlgorithm.Sha256 => "SHA256",
                    HashAlgorithm.Sha512 => "SHA512",
                    _ => throw new ArgumentOutOfRangeException(nameof(Algorithm))
                };

                uri.Append($"&algorithm={algorithmName}");
            }

            if (Digits != Type.GetDefaultDigits())
            {
                uri.Append($"&digits={Digits}");
            }

            if (Type == AuthenticatorType.Totp && Period != Type.GetDefaultPeriod())
            {
                uri.Append($"&period={Period}");
            }

            if (Type == AuthenticatorType.Hotp)
            {
                uri.Append($"&counter={Counter}");
            }

            if (Type == AuthenticatorType.SteamOtp && Issuer != "Steam")
            {
                uri.Append("&steam");
            }

            return uri.ToString();
        }

        public static string CleanSecret(string input, AuthenticatorType type)
        {
            if (type.IsHmacBased())
            {
                input = input.ToUpper();
            }

            input = input.Replace(" ", "");
            input = input.Replace("-", "");

            return input;
        }

        public static bool IsValidSecret(string secret, AuthenticatorType type)
        {
            if (String.IsNullOrEmpty(secret))
            {
                return false;
            }

            if (type.IsHmacBased())
            {
                try
                {
                    var output = Base32.Rfc4648.Decode(secret);
                    return output.Length > 0;
                }
                catch
                {
                    return false;
                }
            }

            if (type == AuthenticatorType.MobileOtp)
            {
                return secret.Length >= MobileOtp.SecretMinLength;
            }

            throw new ArgumentOutOfRangeException(nameof(type));
        }

        public bool IsValid()
        {
            var isValid = !String.IsNullOrEmpty(Issuer) && IsValidSecret(Secret, Type) &&
                          Digits >= Type.GetMinDigits() && Digits <= Type.GetMaxDigits();

            if (Type.GetGenerationMethod() == GenerationMethod.Time)
            {
                isValid = isValid && Period > 0;
            }

            return isValid;
        }
    }
}