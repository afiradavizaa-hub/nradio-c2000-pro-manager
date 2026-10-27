namespace NRadioManager.ViewModels;

/// <summary>下拉选项：Value 为设备实际取值，Text 为界面显示文本。</summary>
public sealed record OptionItem(string Value, string Text)
{
    public override string ToString() => Text;
}
