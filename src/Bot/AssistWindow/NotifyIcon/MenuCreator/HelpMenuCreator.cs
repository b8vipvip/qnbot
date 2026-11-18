using Bot.Common;
using Bot.Common.Db;
using Bot.Common.Windows;
using Bot.Options;
using Bot.ShopScope;
using BotLib;
using BotLib.Extensions;
using BotLib.Wpf.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Bot.AssistWindow.NotifyIcon.MenuCreator
{
    public class HelpMenuCreator
    {
        public static void Create(CtlNotifyIcon notifyIcon)
        {
            var helpRootMenu = notifyIcon.GetRootMenu(0);
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("打开安装目录", OnOpenAppCatalogClicked));
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("打开data目录", OnOpenDataCatalogClicked));
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateSeparator());
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("打开日志", OnShowLogClicked));
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("清空日志", OnClearLogClicked));
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateSeparator());
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("服务端配置", OnServerConfigClicked));
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateSeparator());
            helpRootMenu.DropDownItems.Add(notifyIcon.CreateItem("关于与版本更新", OnAboutClicked));
        }

        private static void OnComeNoodles(object sender, EventArgs e)
        {
            WndNoodles.MyShow();
        }

        private static void OnComeRedBull(object sender, EventArgs e)
        {
            WndRedBull.MyShow();
        }

        private static void OnClearLogClicked(object sender, EventArgs e)
        {
            Log.Clear();
        }

        private static void OnShowLogClicked(object sender, EventArgs e)
        {
            Log.Show();
        }

        private static void OnAboutClicked(object sender, EventArgs e)
        {
            BotAboutUpdateLauncher.Show();
        }

        private static void OnServerConfigClicked(object sender, EventArgs e)
        {
            try
            {
                using (var dialog = new Form())
                {
                    dialog.Text = "Bot 服务端配置";
                    dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                    dialog.StartPosition = FormStartPosition.CenterScreen;
                    dialog.ClientSize = new Size(500, 190);
                    dialog.MaximizeBox = false;
                    dialog.MinimizeBox = false;
                    dialog.ShowInTaskbar = false;

                    var label = new Label
                    {
                        Text = "统一服务端地址：",
                        AutoSize = true,
                        Location = new Point(18, 18)
                    };
                    var address = new TextBox
                    {
                        Location = new Point(18, 44),
                        Width = 464,
                        Text = ShopControlPlaneConnectionStore.GetLegacyGlobalServerUrl()
                    };
                    var hint = new Label
                    {
                        Text = "所有店铺共用此地址，店铺 Token 和本地数据仍独立保存。可直接输入域名，默认使用 HTTPS。",
                        AutoSize = false,
                        Location = new Point(18, 76),
                        Size = new Size(464, 38)
                    };
                    var test = new Button
                    {
                        Text = "测试连接",
                        Location = new Point(18, 137),
                        Size = new Size(88, 30)
                    };
                    var reset = new Button
                    {
                        Text = "恢复默认",
                        Location = new Point(112, 137),
                        Size = new Size(88, 30)
                    };
                    var cancel = new Button
                    {
                        Text = "取消",
                        Location = new Point(300, 137),
                        Size = new Size(80, 30),
                        DialogResult = DialogResult.Cancel
                    };
                    var save = new Button
                    {
                        Text = "保存",
                        Location = new Point(392, 137),
                        Size = new Size(90, 30)
                    };

                    test.Click += delegate
                    {
                        string normalized;
                        string error;
                        if (!ShopControlPlaneConnectionStore.TryNormalizeServerUrl(address.Text, out normalized, out error))
                        {
                            MessageBox.Show(dialog, error, "服务端地址无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        address.Text = normalized;
                        var previousCursor = Cursor.Current;
                        test.Enabled = false;
                        try
                        {
                            Cursor.Current = Cursors.WaitCursor;
                            var request = (HttpWebRequest)WebRequest.Create(normalized.TrimEnd('/') + "/healthz");
                            request.Method = "GET";
                            request.Timeout = 8000;
                            request.ReadWriteTimeout = 8000;
                            request.AllowAutoRedirect = true;
                            request.UserAgent = "QianniuAiBot-ServerConfig";
                            using (var response = (HttpWebResponse)request.GetResponse())
                            {
                                if (response.StatusCode == HttpStatusCode.OK)
                                {
                                    MessageBox.Show(dialog, "服务端健康检查成功。\n" + normalized,
                                        "连接成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                }
                                else
                                {
                                    MessageBox.Show(dialog, "服务端已响应，但健康检查返回 HTTP "
                                        + (int)response.StatusCode + " " + response.StatusDescription,
                                        "连接检查", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                }
                            }
                        }
                        catch (WebException ex)
                        {
                            var response = ex.Response as HttpWebResponse;
                            var detail = response == null
                                ? ex.Message
                                : "HTTP " + (int)response.StatusCode + " " + response.StatusDescription;
                            MessageBox.Show(dialog, "无法通过 /healthz 检查服务端：\n" + detail,
                                "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        catch (Exception ex)
                        {
                            Log.Exception(ex);
                            MessageBox.Show(dialog, "连接检查失败：\n" + ex.Message,
                                "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        finally
                        {
                            Cursor.Current = previousCursor;
                            test.Enabled = true;
                        }
                    };

                    reset.Click += delegate
                    {
                        address.Text = ShopControlPlaneConnectionStore.GetBuiltInServerUrl();
                        address.Focus();
                        address.SelectAll();
                    };

                    save.Click += delegate
                    {
                        string normalized;
                        string error;
                        if (!ShopControlPlaneConnectionStore.TryNormalizeServerUrl(address.Text, out normalized, out error))
                        {
                            MessageBox.Show(dialog, error, "服务端地址无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        try
                        {
                            ShopControlPlaneConnectionStore.SaveProgramServerUrl(normalized);
                            Log.Info("托盘服务端配置已更新: " + normalized);
                            MessageBox.Show(dialog,
                                "服务端地址已保存：\n" + normalized
                                + "\n\n后续请求将使用新地址。正在运行的旧连接可能需要重启客户端后完全切换。",
                                "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            dialog.DialogResult = DialogResult.OK;
                        }
                        catch (Exception ex)
                        {
                            Log.Exception(ex);
                            MessageBox.Show(dialog, "保存服务端地址失败：\n" + ex.Message,
                                "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    };

                    dialog.Controls.Add(label);
                    dialog.Controls.Add(address);
                    dialog.Controls.Add(hint);
                    dialog.Controls.Add(test);
                    dialog.Controls.Add(reset);
                    dialog.Controls.Add(cancel);
                    dialog.Controls.Add(save);
                    dialog.AcceptButton = save;
                    dialog.CancelButton = cancel;
                    dialog.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                MessageBox.Show("打开服务端配置失败：" + ex.Message,
                    "QnBot", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void OnOpenDataCatalogClicked(object sender, EventArgs e)
        {
            try
            {
                Process.Start(PathEx.DataDir);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        private static void OnOpenAppCatalogClicked(object sender, EventArgs e)
        {
            try
            {
                Process.Start(PathEx.StartUpPathOfExe);
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }
    }
}
