using Avalonia.Markup.Xaml;
using MaoX.Core;

namespace MaoX.Controls;

/// <summary>XAML 里的翻译：Text="{c:Tr 启动游戏}"，含英文逗号时加引号 {c:Tr '一，二'}。</summary>
public class TrExtension : MarkupExtension
{
    public TrExtension(string text)
    {
        Text = text;
    }

    public string Text { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => I18n.T(Text);
}
