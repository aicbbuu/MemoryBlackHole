using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MemoryBlackHole.Services
{
    /// <summary>简单的 Markdown 转 FlowDocument 渲染器。</summary>
    public static class MarkdownHelper
    {
        /// <summary>将 Markdown 文本转换为 FlowDocument。</summary>
        public static FlowDocument ToFlowDocument(string markdown, bool darkMode = true)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = 14,
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei"),
                Foreground = darkMode ? new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF5)) : Brushes.Black
            };

            if (string.IsNullOrWhiteSpace(markdown))
            {
                doc.Blocks.Add(new Paragraph(new Run("(空内容)")));
                return doc;
            }

            var lines = markdown.Split('\n');
            Paragraph? currentPara = null;
            List list = null!;
            bool inCodeBlock = false;
            string codeBlockContent = "";

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // 代码块
                if (trimmed.StartsWith("```"))
                {
                    if (inCodeBlock)
                    {
                        // 结束代码块
                        var codeBlock = new Paragraph
                        {
                            Background = darkMode ? new SolidColorBrush(Color.FromRgb(0x1A, 0x20, 0x35)) : new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                            FontSize = 12,
                            Margin = new Thickness(0, 4, 0, 4),
                            Padding = new Thickness(12, 8, 12, 8)
                        };
                        // Use a Run with LineBreak for each actual line
                        var codeLines = codeBlockContent.TrimEnd('\n').Split('\n');
                        for (int i = 0; i < codeLines.Length; i++)
                        {
                            codeBlock.Inlines.Add(new Run(codeLines[i]));
                            if (i < codeLines.Length - 1)
                                codeBlock.Inlines.Add(new LineBreak());
                        }
                        doc.Blocks.Add(codeBlock);
                        codeBlockContent = "";
                        inCodeBlock = false;
                    }
                    else
                    {
                        inCodeBlock = true;
                        codeBlockContent = "";
                        currentPara = null;
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    codeBlockContent += line + "\n";
                    continue;
                }

                // 空行
                if (string.IsNullOrWhiteSpace(line))
                {
                    currentPara = null;
                    continue;
                }

                // 标题
                if (trimmed.StartsWith("### "))
                {
                    var h = new Paragraph(new Run(trimmed[4..]))
                    {
                        FontSize = 16,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, 10, 0, 4)
                    };
                    doc.Blocks.Add(h);
                    currentPara = null;
                    continue;
                }
                if (trimmed.StartsWith("## "))
                {
                    var h = new Paragraph(new Run(trimmed[3..]))
                    {
                        FontSize = 19,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, 12, 0, 4)
                    };
                    doc.Blocks.Add(h);
                    currentPara = null;
                    continue;
                }
                if (trimmed.StartsWith("# "))
                {
                    var h = new Paragraph(new Run(trimmed[2..]))
                    {
                        FontSize = 22,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, 14, 0, 6)
                    };
                    doc.Blocks.Add(h);
                    currentPara = null;
                    continue;
                }

                // 无序列表
                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                {
                    var content = trimmed[2..];
                    if (list == null)
                    {
                        list = new List
                        {
                            MarkerStyle = TextMarkerStyle.Disc,
                            Margin = new Thickness(20, 0, 0, 0)
                        };
                        doc.Blocks.Add(list);
                    }
                    var li = new ListItem(new Paragraph(ParseInline(content, darkMode)));
                    list.ListItems.Add(li);
                    currentPara = null;
                    continue;
                }
                list = null!;

                // 有序列表
                if (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && trimmed[1] == '.')
                {
                    var content = trimmed[2..].TrimStart();
                    if (list == null)
                    {
                        list = new List
                        {
                            MarkerStyle = TextMarkerStyle.Decimal,
                            Margin = new Thickness(20, 0, 0, 0)
                        };
                        doc.Blocks.Add(list);
                    }
                    var li = new ListItem(new Paragraph(ParseInline(content, darkMode)));
                    list.ListItems.Add(li);
                    currentPara = null;
                    continue;
                }
                list = null!;

                // 普通段落
                if (currentPara == null)
                {
                    currentPara = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                    doc.Blocks.Add(currentPara);
                }
                else
                {
                    currentPara.Inlines.Add(new LineBreak());
                }

                // 解析行内标记
                foreach (var inline in ParseInline(trimmed, darkMode))
                    currentPara.Inlines.Add(inline);
            }

            return doc;
        }

        private static List<Inline> ParseInline(string text, bool darkMode)
        {
            var result = new List<Inline>();
            int i = 0;
            while (i < text.Length)
            {
                // 粗体 **text** 或 __text__
                if ((i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*') ||
                    (i + 1 < text.Length && text[i] == '_' && text[i + 1] == '_'))
                {
                    char c = text[i];
                    int end = text.IndexOf($"{c}{c}", i + 2);
                    if (end > i + 2)
                    {
                        var bold = new Bold(new Run(text[(i + 2)..end]));
                        result.Add(bold);
                        i = end + 2;
                        continue;
                    }
                }
                // 斜体 *text* 或 _text_
                if (text[i] == '*' || text[i] == '_')
                {
                    char c = text[i];
                    int end = text.IndexOf(c, i + 1);
                    if (end > i + 1 && text[end - 1] != c) // 不是粗体
                    {
                        var italic = new Italic(new Run(text[(i + 1)..end]));
                        result.Add(italic);
                        i = end + 1;
                        continue;
                    }
                }
                // 行内代码 `code`
                if (text[i] == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i + 1)
                    {
                        var code = new Run(text[(i + 1)..end])
                        {
                            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                            Background = darkMode ? new SolidColorBrush(Color.FromRgb(0x1A, 0x20, 0x35)) : new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6))
                        };
                        result.Add(code);
                        i = end + 1;
                        continue;
                    }
                }
                // 普通文本
                int next = i + 1;
                result.Add(new Run(text[i].ToString()));
                i++;
            }
            return result;
        }
    }
}