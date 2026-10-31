using System;
using System.Windows;
using System.Windows.Controls;

namespace Bot.AssistWindow.Widget.Robot
{
    internal sealed class ConversationEditWindow : Window
    {
        private readonly TextBox _answer;
        private readonly TextBox _question;
        public string EditedQuestion { get { return (_question.Text ?? string.Empty).Trim(); } }
        public string EditedAnswer { get { return (_answer.Text ?? string.Empty).Trim(); } }

        public ConversationEditWindow(string question, string answer)
        {
            Title = "修改答案";
            Width = 620;
            Height = 430;
            MinWidth = 520;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var cancel = new Button { Content = "取消", Width = 86, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += (s, e) => { DialogResult = false; };
            var save = new Button { Content = "保存修改", Width = 96, Height = 30, IsDefault = true };
            save.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(EditedAnswer) || string.IsNullOrWhiteSpace(EditedQuestion))
                {
                    MessageBox.Show("问题和答案不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                // An isolated image placeholder or short dependent question is never
                // a valid global override. Operator must define a standalone scope.
                var parsed = Bot.Knowledge.KnowledgeEngineV2Semantics.Parse(EditedQuestion, null);
                if (EditedQuestion == "[图片]" || parsed.ContextDependent
                    || string.IsNullOrWhiteSpace(parsed.Subject))
                {
                    MessageBox.Show("请在“适用问题”中补充明确业务主体与场景。"
                        + "\n例如：酷狗会员支持电视端使用吗？"
                        + "\n[图片]或“是电视用的吗？”不能对所有买家无条件优先回复。",
                        "人工最高优先直答范围", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);

            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(90) });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(panel);

            var qLabel = new TextBlock { Text = "适用问题（可编辑；补全业务主体后成为人工最高优先精确直答）", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(qLabel);
            _question = new TextBox { Text = question ?? string.Empty, IsReadOnly = false,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8),
                ToolTip = "将图片占位符或省略提问补全为独立、准确的业务问题，精确命中时优先于普通知识。" };
            Grid.SetRow(_question, 1);
            panel.Children.Add(_question);
            var aLabel = new TextBlock { Text = "修改后的答案", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 6) };
            Grid.SetRow(aLabel, 2);
            panel.Children.Add(aLabel);
            _answer = new TextBox { Text = answer ?? string.Empty, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8) };
            Grid.SetRow(_answer, 3);
            panel.Children.Add(_answer);
        }
    }
}
