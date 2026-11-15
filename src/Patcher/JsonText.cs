// Surgical JSON translator: replaces selected string values and copies every other character of the
// document verbatim (numbers, ordering, whitespace and escapes of untouched strings stay byte-identical).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RepoRPG_RU
{
    internal delegate string JsonLookup(string path, string property, string value);

    internal static class JsonText
    {
        private sealed class Frame
        {
            public bool IsObject;
            public bool ExpectKey;
            public string Label;      // property name the next value belongs to (arrays inherit it)
            public int PathLength;    // length of the path builder before this frame's key was appended
        }

        public static string Translate(string json, JsonLookup lookup, out int replaced)
        {
            replaced = 0;
            StringBuilder output = null;
            int copied = 0;
            List<Frame> stack = new List<Frame>();
            StringBuilder path = new StringBuilder();
            int i = 0, n = json.Length;
            while (i < n)
            {
                char c = json[i];
                Frame top = stack.Count > 0 ? stack[stack.Count - 1] : null;
                switch (c)
                {
                    case '{':
                    case '[':
                    {
                        Frame f = new Frame();
                        f.IsObject = c == '{';
                        f.ExpectKey = f.IsObject;
                        f.Label = top == null ? null : top.Label;
                        f.PathLength = path.Length;
                        stack.Add(f);
                        i++;
                        break;
                    }
                    case '}':
                    case ']':
                        if (stack.Count > 0)
                        {
                            path.Length = Math.Min(path.Length, top.PathLength);
                            stack.RemoveAt(stack.Count - 1);
                        }
                        i++;
                        break;
                    case ',':
                        if (top != null && top.IsObject)
                        {
                            top.ExpectKey = true;
                            path.Length = Math.Min(path.Length, top.PathLength);
                        }
                        i++;
                        break;
                    case ':':
                        if (top != null) top.ExpectKey = false;
                        i++;
                        break;
                    case '"':
                    {
                        int end;
                        string value = ReadString(json, i, out end);
                        if (top != null && top.IsObject && top.ExpectKey)
                        {
                            top.Label = value;
                            path.Length = Math.Min(path.Length, top.PathLength);
                            if (path.Length > 0) path.Append('/');
                            path.Append(value);
                        }
                        else if (top != null && top.Label != null && value.Length > 0)
                        {
                            string translated = lookup(path.ToString(), top.Label, value);
                            if (translated != null && !string.Equals(translated, value, StringComparison.Ordinal))
                            {
                                if (output == null) output = new StringBuilder(json.Length + 1024);
                                output.Append(json, copied, i - copied);
                                WriteString(output, translated);
                                copied = end;
                                replaced++;
                            }
                        }
                        i = end;
                        break;
                    }
                    default:
                        i++;
                        break;
                }
            }
            if (output == null) return json;
            output.Append(json, copied, n - copied);
            return output.ToString();
        }

        /// <summary>Reads the string token starting at the quote at <paramref name="start"/>; <paramref name="end"/> is the index after the closing quote.</summary>
        private static string ReadString(string s, int start, out int end)
        {
            StringBuilder sb = new StringBuilder();
            int i = start + 1, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == '"') { end = i + 1; return sb.ToString(); }
                if (c == '\\' && i + 1 < n)
                {
                    char d = s[i + 1];
                    i += 2;
                    switch (d)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            int code;
                            if (i + 4 <= n && int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            else sb.Append('u');
                            break;
                        default: sb.Append(d); break;   // \" \\ \/
                    }
                    continue;
                }
                sb.Append(c);
                i++;
            }
            end = n;
            return sb.ToString();
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
