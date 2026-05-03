using LemonLite.Configs;
using LemonLite.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LemonLite.Views.UserControls;

public partial class HotkeyRecorder : UserControl
{
    private bool _isRecording;
    private HotkeyModifiers _currentModifiers;
    private int _currentKey;
    private HotkeyBinding? _originalBinding;

    public static readonly DependencyProperty HotkeyBindingProperty =
        DependencyProperty.Register(nameof(HotkeyBinding), typeof(HotkeyBinding), typeof(HotkeyRecorder),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyBindingChanged));

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(nameof(PlaceholderText), typeof(string), typeof(HotkeyRecorder),
            new PropertyMetadata(""));

    public static readonly DependencyProperty HasConflictProperty =
        DependencyProperty.Register(nameof(HasConflict), typeof(bool), typeof(HotkeyRecorder),
            new PropertyMetadata(false, OnHasConflictChanged));

    public HotkeyBinding HotkeyBinding
    {
        get => (HotkeyBinding)GetValue(HotkeyBindingProperty);
        set => SetValue(HotkeyBindingProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public bool HasConflict
    {
        get => (bool)GetValue(HasConflictProperty);
        set => SetValue(HasConflictProperty, value);
    }

    public HotkeyRecorder()
    {
        InitializeComponent();
        UpdateDisplay();
    }

    private static void OnHotkeyBindingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyRecorder recorder)
            recorder.UpdateDisplay();
    }

    private static void OnHasConflictChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyRecorder recorder)
            recorder.UpdateBorderAppearance();
    }

    private void RootBorder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        e.Handled = true;
    }

    private void UpdateDisplay()
    {
        if (DisplayText == null) return;

        if (_isRecording)
        {
            DisplayText.Text = LocalizationService.Instance["PressKeysHint"];
            PlaceholderTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        if (HotkeyBinding != null && !HotkeyBinding.IsEmpty)
        {
            DisplayText.Text = HotkeyBinding.ToDisplayString();
            PlaceholderTextBlock.Visibility = Visibility.Collapsed;
        }
        else
        {
            DisplayText.Text = "";
            PlaceholderTextBlock.Text = PlaceholderText;
            PlaceholderTextBlock.Visibility = string.IsNullOrEmpty(PlaceholderText) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void UpdateBorderAppearance()
    {
        if (RootBorder == null) return;

        if (_isRecording)
        {
            RootBorder.BorderBrush = (Brush)FindResource("FocusMaskColor");
            RootBorder.BorderThickness = new Thickness(2);
        }
        else if (HasConflict)
        {
            RootBorder.BorderBrush = Brushes.OrangeRed;
            RootBorder.BorderThickness = new Thickness(2);
        }
        else
        {
            RootBorder.BorderBrush = (Brush)FindResource("BorderColor");
            RootBorder.BorderThickness = new Thickness(1);
        }
    }

    private void HotkeyRecorder_GotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != this) return;
        _isRecording = true;
        _originalBinding = HotkeyBinding?.Clone();
        _currentModifiers = HotkeyModifiers.None;
        _currentKey = 0;
        UpdateDisplay();
        UpdateBorderAppearance();
    }

    private void HotkeyRecorder_LostFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != this) return;
        if (_isRecording)
        {
            _isRecording = false;
            if (_currentKey != 0 && _currentModifiers != HotkeyModifiers.None)
            {
                HotkeyBinding = new HotkeyBinding { Modifiers = _currentModifiers, Key = _currentKey };
            }
            else if (_originalBinding != null)
            {
                HotkeyBinding = _originalBinding!;
            }
            UpdateDisplay();
            UpdateBorderAppearance();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_isRecording)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.LeftCtrl:
            case Key.RightCtrl:
                _currentModifiers |= HotkeyModifiers.Ctrl;
                UpdateRecordingDisplay();
                return;
            case Key.LeftAlt:
            case Key.RightAlt:
                _currentModifiers |= HotkeyModifiers.Alt;
                UpdateRecordingDisplay();
                return;
            case Key.LeftShift:
            case Key.RightShift:
                _currentModifiers |= HotkeyModifiers.Shift;
                UpdateRecordingDisplay();
                return;
            case Key.LWin:
            case Key.RWin:
                _currentModifiers |= HotkeyModifiers.Win;
                UpdateRecordingDisplay();
                return;
            case Key.Escape:
                _currentModifiers = HotkeyModifiers.None;
                _currentKey = 0;
                HotkeyBinding = _originalBinding!;
                _isRecording = false;
                UpdateDisplay();
                UpdateBorderAppearance();
                return;
            case Key.Back:
                if (_currentKey != 0 || _currentModifiers != HotkeyModifiers.None)
                {
                    _currentModifiers = HotkeyModifiers.None;
                    _currentKey = 0;
                    UpdateRecordingDisplay();
                    return;
                }
                HotkeyBinding = new HotkeyBinding();
                _isRecording = false;
                UpdateDisplay();
                UpdateBorderAppearance();
                return;
        }

        if (_currentModifiers == HotkeyModifiers.None)
        {
            _currentModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt;
        }

        var vk = KeyToVirtualKey(key);
        if (vk > 0)
        {
            _currentKey = vk;
            HotkeyBinding = new HotkeyBinding { Modifiers = _currentModifiers, Key = _currentKey };
            _isRecording = false;
            UpdateDisplay();
            UpdateBorderAppearance();
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!_isRecording)
        {
            base.OnPreviewKeyUp(e);
            return;
        }
        e.Handled = true;
    }

    private void UpdateRecordingDisplay()
    {
        if (DisplayText == null) return;

        var temp = new HotkeyBinding { Modifiers = _currentModifiers, Key = _currentKey };
        if (temp.IsEmpty)
        {
            DisplayText.Text = LocalizationService.Instance["PressKeysHint"];
        }
        else
        {
            DisplayText.Text = temp.ToDisplayString() + " ...";
        }
    }

    private static int KeyToVirtualKey(Key key)
    {
        return KeyInterop.VirtualKeyFromKey(key);
    }
}
