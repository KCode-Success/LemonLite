using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using LemonLite.Configs;
using LemonLite.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace LemonLite.Services
{
    /// <summary>
    /// 全局液态玻璃开关服务（单例）。
    /// 暴露 <see cref="IsEnabled"/> 与 <see cref="CurrentToggleButtonStyle"/> 给所有 SettingsPage 绑定。
    /// 切换时通过 <see cref="ObservableObject"/> 自动通知。
    /// 持久化到 <see cref="AppOption.EnableLiquidGlass"/>。
    /// </summary>
    public sealed partial class LiquidGlassService : ObservableObject
    {
        public static LiquidGlassService Instance { get; } = new();

        private readonly SettingsMgr<AppOption>? _appOption;
        private bool _isEnabled;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentToggleButtonStyle));
                try
                {
                    if (_appOption != null)
                    {
                        _appOption.Data.EnableLiquidGlass = value;
                        _appOption.TriggerDataChanged();
                    }
                }
                catch { /* 设计期或服务未就绪，忽略 */ }
            }
        }

        /// <summary>
        /// 当前应使用的 ToggleButton Style。
        /// 开启液态玻璃时返回 <c>LiquidGlassToggleButtonStyle</c>，否则返回 <c>RoundToggleButtonStyle</c>。
        /// 在 UI 线程访问；若资源未就绪（设计期/启动早期）回退到 Round。
        /// </summary>
        public Style CurrentToggleButtonStyle
        {
            get
            {
                try
                {
                    if (IsEnabled && Application.Current != null)
                    {
                        return (Style)Application.Current.FindResource("LiquidGlassToggleButtonStyle");
                    }
                }
                catch { /* 资源未就绪 */ }
                try
                {
                    return (Style)Application.Current.FindResource("RoundToggleButtonStyle");
                }
                catch
                {
                    return null!;
                }
            }
        }

        private LiquidGlassService()
        {
            try
            {
                _appOption = App.Services?.GetService<AppSettingService>()?.GetConfigMgr<AppOption>();
                if (_appOption != null)
                {
                    _isEnabled = _appOption.Data.EnableLiquidGlass;
                }
            }
            catch { /* 设计期 / 测试期不报错 */ }
        }
    }
}

