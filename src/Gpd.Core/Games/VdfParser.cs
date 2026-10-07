// ============================================================================
//  Gpd.Core / Games / VdfParser.cs
//  ---------------------------------------------------------------------------
//  Valve KeyValues（VDF）格式的最小解析器。
//  Steam 的 libraryfolders.vdf / appmanifest_*.acf / localconfig.vdf 都是这个格式。
//  只依赖 BCL，不引入任何第三方包；只解析字符串，不读写任何文件（ParseFile 只读）。
// ============================================================================

using System.Text;

namespace Gpd.Core.Games;

/// <summary>VDF（KeyValues）树节点。键名比较不区分大小写（和 Steam 行为一致）。</summary>
public sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode> _index = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode(string key) => Key = key;

    /// <summary>本节点的键名。</summary>
    public string Key { get; }

    /// <summary>标量值；为对象节点时为 null。</summary>
    public string? Value { get; set; }

    /// <summary>子节点（保持文件中的先后顺序）。</summary>
    public List<VdfNode> Children { get; } = new();

    /// <summary>是否是一个 { } 对象节点。</summary>
    public bool IsObject => Value is null;

    public VdfNode AddChild(VdfNode child)
    {
        Children.Add(child);
        // Steam 对重复键取"后出现的那个"，这里保持一致。
        _index[child.Key] = child;
        return child;
    }

    /// <summary>按名取直接子节点（不区分大小写），找不到返回 null。</summary>
    public VdfNode? Child(string name) => _index.TryGetValue(name, out var node) ? node : null;

    /// <summary>按名取直接子节点的标量值，找不到返回 null。</summary>
    public string? ChildValue(string name) => Child(name)?.Value;

    /// <summary>按路径逐级取子节点，任一级缺失返回 null。</summary>
    public VdfNode? Path(params string[] keys)
    {
        VdfNode? current = this;
        foreach (var key in keys)
        {
            if (current is null)
            {
                return null;
            }

            current = current.Child(key);
        }

        return current;
    }

    /// <summary>深度优先枚举全部后代（不含自身）。</summary>
    public IEnumerable<VdfNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var grandChild in child.Descendants())
            {
                yield return grandChild;
            }
        }
    }
}

/// <summary>VDF 文本解析器。语法错误会抛 <see cref="FormatException"/>，调用方自行兜底。</summary>
public static class VdfParser
{
    private enum TokenKind
    {
        Scalar,
        OpenBrace,
        CloseBrace,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    /// <summary>读取并解析一个 VDF 文件（只读）。失败返回 null，不抛异常。</summary>
    public static VdfNode? ParseFile(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception)
        {
            // 扫描器要求"绝不因为一个坏文件崩掉整个流程"。
            return null;
        }
    }

    /// <summary>解析 VDF 文本，返回一个包含全部顶层节点的虚拟根节点。</summary>
    public static VdfNode Parse(string text, string rootKey = "$root")
    {
        var tokens = Tokenize(text);
        var root = new VdfNode(rootKey);
        var position = 0;

        while (tokens[position].Kind != TokenKind.End)
        {
            if (tokens[position].Kind == TokenKind.CloseBrace)
            {
                throw new FormatException("VDF 语法错误：出现多余的 '}'。");
            }

            root.AddChild(ParseNode(tokens, ref position));
        }

        return root;
    }

    private static VdfNode ParseNode(List<Token> tokens, ref int position)
    {
        var keyToken = tokens[position];
        if (keyToken.Kind != TokenKind.Scalar)
        {
            throw new FormatException($"VDF 语法错误：第 {position} 个记号应为键名，实际为 {keyToken.Kind}。");
        }

        position++;
        var node = new VdfNode(keyToken.Text);

        if (position < tokens.Count && tokens[position].Kind == TokenKind.OpenBrace)
        {
            position++;
            while (position < tokens.Count
                   && tokens[position].Kind != TokenKind.CloseBrace
                   && tokens[position].Kind != TokenKind.End)
            {
                node.AddChild(ParseNode(tokens, ref position));
            }

            if (position < tokens.Count && tokens[position].Kind == TokenKind.CloseBrace)
            {
                position++;
            }
            else
            {
                throw new FormatException($"VDF 语法错误：节点 \"{node.Key}\" 缺少配对的 '}}'。");
            }
        }
        else if (position < tokens.Count && tokens[position].Kind == TokenKind.Scalar)
        {
            node.Value = tokens[position].Text;
            position++;
        }
        else
        {
            // "key" 后面什么都没有：当作空值，不让整个文件作废。
            node.Value = string.Empty;
        }

        return node;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        var length = text.Length;

        while (index < length)
        {
            var c = text[index];

            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }

            // VDF 的注释是 // 到行尾（Steam 的文件里很常见）。
            if (c == '/' && index + 1 < length && text[index + 1] == '/')
            {
                while (index < length && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (c == '{')
            {
                tokens.Add(new Token(TokenKind.OpenBrace, "{"));
                index++;
                continue;
            }

            if (c == '}')
            {
                tokens.Add(new Token(TokenKind.CloseBrace, "}"));
                index++;
                continue;
            }

            if (c == '"')
            {
                index++;
                var builder = new StringBuilder();
                while (index < length && text[index] != '"')
                {
                    if (text[index] == '\\' && index + 1 < length)
                    {
                        index++;
                        builder.Append(text[index] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '\\' => '\\',
                            '"' => '"',
                            _ => text[index],
                        });
                        index++;
                    }
                    else
                    {
                        builder.Append(text[index]);
                        index++;
                    }
                }

                if (index >= length)
                {
                    throw new FormatException("VDF 语法错误：字符串没有闭合的双引号。");
                }

                index++; // 跳过收尾的 "
                tokens.Add(new Token(TokenKind.Scalar, builder.ToString()));
                continue;
            }

            // 不带引号的裸记号。
            var start = index;
            while (index < length
                   && !char.IsWhiteSpace(text[index])
                   && text[index] != '{'
                   && text[index] != '}'
                   && text[index] != '"')
            {
                index++;
            }

            tokens.Add(new Token(TokenKind.Scalar, text[start..index]));
        }

        tokens.Add(new Token(TokenKind.End, string.Empty));
        return tokens;
    }
}
