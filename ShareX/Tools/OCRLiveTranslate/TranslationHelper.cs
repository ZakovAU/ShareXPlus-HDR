#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2025 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Newtonsoft.Json.Linq;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace ShareX
{
    public class TranslationResult
    {
        public string TranslatedText { get; set; }
        public string DetectedLanguage { get; set; }
        public bool FromCache { get; set; }
    }

    public static class TranslationHelper
    {
        public static readonly OCRLanguage[] TargetLanguages = new OCRLanguage[]
        {
            new OCRLanguage("English", "en"),
            new OCRLanguage("Japanese", "ja"),
            new OCRLanguage("Chinese (Simplified)", "zh-CN"),
            new OCRLanguage("Chinese (Traditional)", "zh-TW"),
            new OCRLanguage("Korean", "ko"),
            new OCRLanguage("Spanish", "es"),
            new OCRLanguage("French", "fr"),
            new OCRLanguage("German", "de"),
            new OCRLanguage("Portuguese", "pt"),
            new OCRLanguage("Russian", "ru"),
            new OCRLanguage("Italian", "it"),
            new OCRLanguage("Arabic", "ar"),
            new OCRLanguage("Vietnamese", "vi"),
            new OCRLanguage("Thai", "th"),
            new OCRLanguage("Polish", "pl"),
            new OCRLanguage("Turkish", "tr"),
            new OCRLanguage("Dutch", "nl"),
            new OCRLanguage("Ukrainian", "uk"),
            new OCRLanguage("Indonesian", "id"),
            new OCRLanguage("Hindi", "hi")
        };

        private static readonly Dictionary<string, TranslationResult> cache = new Dictionary<string, TranslationResult>(StringComparer.Ordinal);
        private static readonly object cacheLock = new object();

        public static string DetectLanguage(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "en";
            }

            int cjk = 0, hiragana = 0, katakana = 0, hangul = 0, cyrillic = 0, arabic = 0, thai = 0, latin = 0;

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsDigit(c))
                {
                    continue;
                }

                if (c >= 0x3040 && c <= 0x309F) hiragana++;
                else if (c >= 0x30A0 && c <= 0x30FF) katakana++;
                else if (c >= 0xAC00 && c <= 0xD7AF) hangul++;
                else if (c >= 0x0400 && c <= 0x04FF) cyrillic++;
                else if (c >= 0x0600 && c <= 0x06FF) arabic++;
                else if (c >= 0x0E00 && c <= 0x0E7F) thai++;
                else if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF)) cjk++;
                else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin++;
            }

            if (hiragana + katakana > 0) return "ja";
            if (hangul > 0) return "ko";
            if (cjk > 0) return "zh";
            if (cyrillic > latin) return "ru";
            if (arabic > latin) return "ar";
            if (thai > latin) return "th";
            return "en";
        }

        public static bool IsSameLanguage(string detected, string target)
        {
            if (string.IsNullOrEmpty(detected) || string.IsNullOrEmpty(target))
            {
                return false;
            }

            string a = detected.Split('-')[0];
            string b = target.Split('-')[0];
            return a.Equals(b, StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<TranslationResult> TranslateAsync(string text, string targetLanguage)
        {
            text = text?.Trim();

            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(targetLanguage))
            {
                return new TranslationResult { TranslatedText = text ?? "", DetectedLanguage = "" };
            }

            string detected = DetectLanguage(text);

            if (IsSameLanguage(detected, targetLanguage))
            {
                return new TranslationResult { TranslatedText = text, DetectedLanguage = detected };
            }

            string cacheKey = targetLanguage + "\n" + text;

            lock (cacheLock)
            {
                if (cache.TryGetValue(cacheKey, out TranslationResult cached))
                {
                    return new TranslationResult
                    {
                        TranslatedText = cached.TranslatedText,
                        DetectedLanguage = cached.DetectedLanguage,
                        FromCache = true
                    };
                }
            }

            if (text.Length > 4500)
            {
                text = text.Substring(0, 4500);
            }

            string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=" +
                URLHelpers.URLEncode(targetLanguage) + "&dt=t&q=" + URLHelpers.URLEncode(text);

            HttpClient client = HttpClientFactory.Create();

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                using (HttpResponseMessage response = await client.SendAsync(request))
                {
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync();
                    TranslationResult result = ParseGoogleTranslateResponse(json, detected);

                    lock (cacheLock)
                    {
                        cache[cacheKey] = result;

                        if (cache.Count > 256)
                        {
                            cache.Clear();
                            cache[cacheKey] = result;
                        }
                    }

                    return result;
                }
            }
        }

        private static TranslationResult ParseGoogleTranslateResponse(string json, string fallbackLanguage)
        {
            JArray root = JArray.Parse(json);
            StringBuilder sb = new StringBuilder();

            if (root.Count > 0 && root[0] is JArray sentences)
            {
                foreach (JToken sentence in sentences)
                {
                    if (sentence is JArray parts && parts.Count > 0 && parts[0].Type == JTokenType.String)
                    {
                        sb.Append(parts[0].ToString());
                    }
                }
            }

            string detected = fallbackLanguage;

            if (root.Count > 2 && root[2].Type == JTokenType.String)
            {
                detected = root[2].ToString();
            }

            return new TranslationResult
            {
                TranslatedText = sb.ToString(),
                DetectedLanguage = detected
            };
        }
    }
}
