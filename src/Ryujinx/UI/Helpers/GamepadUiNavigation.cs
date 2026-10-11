using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Common.Logging;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.UI.App.Common;
using System;
using System.Linq;

namespace Ryujinx.Ava.UI.Helpers
{
    /// <summary>
    /// Provides controller navigation for the Avalonia frontend while no game is running.
    /// The emulated controller path remains untouched during gameplay.
    /// </summary>
    internal sealed class GamepadUiNavigation : IDisposable
    {
        private const float StickThreshold = 0.62f;
        private static readonly TimeSpan InitialRepeatDelay = TimeSpan.FromMilliseconds(330);
        private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(90);

        private readonly MainWindow _mainWindow;
        private readonly IGamepadDriver _driver;
        private readonly DispatcherTimer _timer;

        private IGamepad _gamepad;
        private string _gamepadId;

        private bool _aPressed;
        private bool _bPressed;
        private bool _xPressed;
        private bool _leftShoulderPressed;
        private bool _rightShoulderPressed;

        private readonly RepeatState _up = new();
        private readonly RepeatState _down = new();
        private readonly RepeatState _left = new();
        private readonly RepeatState _right = new();

        public GamepadUiNavigation(MainWindow mainWindow, InputManager inputManager)
        {
            _mainWindow = mainWindow;
            _driver = inputManager.GamepadDriver;

            _driver.OnGamepadConnected += OnGamepadChanged;
            _driver.OnGamepadDisconnected += OnGamepadChanged;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _timer.Tick += Poll;
            _timer.Start();

            RefreshGamepad();
        }

        private void OnGamepadChanged(string id)
        {
            Dispatcher.UIThread.Post(RefreshGamepad, DispatcherPriority.Input);
        }

        private void RefreshGamepad()
        {
            _gamepad?.Dispose();
            _gamepad = null;
            _gamepadId = null;

            ReadOnlySpan<string> ids = _driver.GamepadsIds;

            if (ids.Length > 0)
            {
                _gamepadId = ids[0];
                _gamepad = _driver.GetGamepad(_gamepadId);

                if (_gamepad != null)
                {
                    Logger.Info?.Print(
                        LogClass.UI,
                        $"[UI-GAMEPAD1] Using controller '{_gamepad.Name}' ({_gamepadId}) for frontend navigation.");
                }
            }
            else
            {
                Logger.Info?.Print(LogClass.UI, "[UI-GAMEPAD1] No controller available for frontend navigation.");
            }

            ResetState();
        }

        private void Poll(object sender, EventArgs args)
        {
            if (_gamepad == null || !_gamepad.IsConnected)
            {
                if (_driver.GamepadsIds.Length > 0)
                {
                    RefreshGamepad();
                }

                return;
            }

            GamepadStateSnapshot state;

            try
            {
                state = _gamepad.GetStateSnapshot();
            }
            catch
            {
                RefreshGamepad();
                return;
            }

            // Never consume or duplicate controller actions while the guest is running.
            if (_mainWindow.ViewModel.IsGameRunning)
            {
                RememberButtons(state);
                ResetDirections();
                return;
            }

            (float stickX, float stickY) = state.GetStick(StickInputId.Left);
            DateTime now = DateTime.UtcNow;

            ProcessDirection(
                state.IsPressed(GamepadButtonInputId.DpadUp) || stickY > StickThreshold,
                _up,
                NavigationDirection.Up,
                now);
            ProcessDirection(
                state.IsPressed(GamepadButtonInputId.DpadDown) || stickY < -StickThreshold,
                _down,
                NavigationDirection.Down,
                now);
            ProcessDirection(
                state.IsPressed(GamepadButtonInputId.DpadLeft) || stickX < -StickThreshold,
                _left,
                NavigationDirection.Left,
                now);
            ProcessDirection(
                state.IsPressed(GamepadButtonInputId.DpadRight) || stickX > StickThreshold,
                _right,
                NavigationDirection.Right,
                now);

            ProcessButton(state, GamepadButtonInputId.A, ref _aPressed, ActivateFocusedControl);
            ProcessButton(state, GamepadButtonInputId.B, ref _bPressed, GoBack);
            ProcessButton(state, GamepadButtonInputId.X, ref _xPressed, ShowContextMenu);
            ProcessButton(
                state,
                GamepadButtonInputId.LeftShoulder,
                ref _leftShoulderPressed,
                () => MoveFocus(NavigationDirection.Previous));
            ProcessButton(
                state,
                GamepadButtonInputId.RightShoulder,
                ref _rightShoulderPressed,
                () => MoveFocus(NavigationDirection.Next));
        }

        private static void ProcessButton(
            GamepadStateSnapshot state,
            GamepadButtonInputId button,
            ref bool wasPressed,
            Action action)
        {
            bool pressed = state.IsPressed(button);

            if (pressed && !wasPressed)
            {
                action();
            }

            wasPressed = pressed;
        }

        private void ProcessDirection(
            bool pressed,
            RepeatState repeat,
            NavigationDirection direction,
            DateTime now)
        {
            if (!pressed)
            {
                repeat.IsPressed = false;
                return;
            }

            if (!repeat.IsPressed)
            {
                repeat.IsPressed = true;
                repeat.NextRepeat = now + InitialRepeatDelay;
                MoveFocus(direction);
            }
            else if (now >= repeat.NextRepeat)
            {
                repeat.NextRepeat = now + RepeatInterval;
                MoveFocus(direction);
            }
        }

        private Window GetActiveWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.Windows.LastOrDefault(window => window.IsActive && window.IsVisible)
                    ?? _mainWindow;
            }

            return _mainWindow;
        }

        private void MoveFocus(NavigationDirection direction)
        {
            Window window = GetActiveWindow();
            IInputElement focused = window.FocusManager?.GetFocusedElement();

            if (window == _mainWindow)
            {
                ListBox gameList = FindVisibleGameList(window);

                if (gameList != null &&
                    !IsInside(focused as Control, gameList) &&
                    direction == NavigationDirection.Down)
                {
                    FocusGameList(gameList);
                    return;
                }
            }

            if (focused is not Control focusedControl || !IsAvailable(focusedControl))
            {
                focused = FocusInitialControl(window);
            }

            if (TryMoveSelection(focused, direction))
            {
                return;
            }

            if (window is IInputRoot inputRoot)
            {
                inputRoot.KeyboardNavigationHandler?.Move(
                    focused ?? window,
                    direction,
                    KeyModifiers.None);
            }
        }

        private static ListBox FindVisibleGameList(Window window)
        {
            return window
                .GetVisualDescendants()
                .OfType<ListBox>()
                .FirstOrDefault(listBox =>
                    (listBox.Name is "GameListBox" or "GameGridBox") && IsAvailable(listBox));
        }

        private static bool IsInside(Control control, Control ancestor)
        {
            return control == ancestor ||
                   control?.GetVisualAncestors().OfType<Control>().Contains(ancestor) == true;
        }

        private static void FocusGameList(ListBox listBox)
        {
            if (listBox.ItemCount > 0 && listBox.SelectedIndex < 0)
            {
                listBox.SelectedIndex = 0;
            }

            if (listBox.SelectedIndex >= 0)
            {
                listBox.ScrollIntoView(listBox.Items[listBox.SelectedIndex]);
            }

            listBox.Focus(NavigationMethod.Directional);
        }

        private static IInputElement FocusInitialControl(Window window)
        {
            Control initial = window
                .GetVisualDescendants()
                .OfType<ListBox>()
                .FirstOrDefault(IsAvailable);

            initial ??= window
                .GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(control => control.Focusable && IsAvailable(control));

            if (initial is ListBox listBox && listBox.ItemCount > 0 && listBox.SelectedIndex < 0)
            {
                listBox.SelectedIndex = 0;
                listBox.ScrollIntoView(listBox.Items[0]);
            }

            initial?.Focus(NavigationMethod.Directional);
            return initial;
        }

        private static bool IsAvailable(Control control)
        {
            return control.IsVisible &&
                   control.IsEnabled &&
                   control.GetVisualAncestors().OfType<Control>().All(parent => parent.IsVisible && parent.IsEnabled);
        }

        private static bool TryMoveSelection(IInputElement focused, NavigationDirection direction)
        {
            Control control = focused as Control;
            ComboBox comboBox = control as ComboBox
                ?? control?.GetVisualAncestors().OfType<ComboBox>().FirstOrDefault();

            if (comboBox?.IsDropDownOpen == true &&
                direction is NavigationDirection.Up or NavigationDirection.Down)
            {
                int comboDelta = direction == NavigationDirection.Up ? -1 : 1;
                comboBox.SelectedIndex = Math.Clamp(
                    comboBox.SelectedIndex + comboDelta,
                    0,
                    comboBox.ItemCount - 1);
                return true;
            }

            ListBox listBox = control as ListBox
                ?? control?.GetVisualAncestors().OfType<ListBox>().FirstOrDefault();

            if (listBox == null || listBox.ItemCount == 0)
            {
                return false;
            }

            int delta;

            if (listBox.Name == "GameGridBox")
            {
                int columns = GetGridColumnCount(listBox);
                delta = direction switch
                {
                    NavigationDirection.Left => -1,
                    NavigationDirection.Right => 1,
                    NavigationDirection.Up => -columns,
                    NavigationDirection.Down => columns,
                    _ => 0,
                };
            }
            else
            {
                delta = direction switch
                {
                    NavigationDirection.Up => -1,
                    NavigationDirection.Down => 1,
                    _ => 0,
                };
            }

            if (delta == 0)
            {
                return false;
            }

            int current = listBox.SelectedIndex < 0 ? 0 : listBox.SelectedIndex;
            int next = Math.Clamp(current + delta, 0, listBox.ItemCount - 1);

            if (next == current)
            {
                return false;
            }

            listBox.SelectedIndex = next;
            listBox.ScrollIntoView(listBox.Items[next]);
            listBox.Focus(NavigationMethod.Directional);
            return true;
        }

        private static int GetGridColumnCount(ListBox listBox)
        {
            Control first = listBox.ContainerFromIndex(0) as Control;

            if (first == null || first.Bounds.Width <= 1)
            {
                return 5;
            }

            return Math.Max(1, (int)(listBox.Bounds.Width / first.Bounds.Width));
        }

        private void ActivateFocusedControl()
        {
            Window window = GetActiveWindow();
            IInputElement focused = window.FocusManager?.GetFocusedElement();

            if (focused is not Control focusedControl || !IsAvailable(focusedControl))
            {
                focused = FocusInitialControl(window);
            }

            Control control = focused as Control;

            ComboBox comboBox = control as ComboBox
                ?? control?.GetVisualAncestors().OfType<ComboBox>().FirstOrDefault();
            if (comboBox != null)
            {
                comboBox.IsDropDownOpen = !comboBox.IsDropDownOpen;
                return;
            }

            ToggleButton toggleButton = control as ToggleButton
                ?? control?.GetVisualAncestors().OfType<ToggleButton>().FirstOrDefault();
            if (toggleButton != null)
            {
                toggleButton.IsChecked = toggleButton.IsChecked != true;
                toggleButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                return;
            }

            Avalonia.Controls.Button button = control as Avalonia.Controls.Button
                ?? control?.GetVisualAncestors().OfType<Avalonia.Controls.Button>().FirstOrDefault();
            if (button != null)
            {
                button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                return;
            }

            MenuItem menuItem = control as MenuItem
                ?? control?.GetVisualAncestors().OfType<MenuItem>().FirstOrDefault();
            if (menuItem != null)
            {
                menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return;
            }

            ListBox listBox = control as ListBox
                ?? control?.GetVisualAncestors().OfType<ListBox>().FirstOrDefault();

            if (window == _mainWindow && listBox?.SelectedItem is ApplicationData application)
            {
                _ = _mainWindow.ViewModel.LoadApplication(application);
            }
        }

        private void GoBack()
        {
            Window window = GetActiveWindow();
            IInputElement focused = window.FocusManager?.GetFocusedElement();
            Control control = focused as Control;
            ComboBox comboBox = control as ComboBox
                ?? control?.GetVisualAncestors().OfType<ComboBox>().FirstOrDefault();

            if (comboBox?.IsDropDownOpen == true)
            {
                comboBox.IsDropDownOpen = false;
            }
            else if (window != _mainWindow)
            {
                window.Close();
            }
        }

        private void ShowContextMenu()
        {
            Window window = GetActiveWindow();
            IInputElement focused = window.FocusManager?.GetFocusedElement();
            Control control = focused as Control;
            ListBox listBox = control as ListBox
                ?? control?.GetVisualAncestors().OfType<ListBox>().FirstOrDefault();

            listBox?.ContextFlyout?.ShowAt(listBox);
        }

        private void RememberButtons(GamepadStateSnapshot state)
        {
            _aPressed = state.IsPressed(GamepadButtonInputId.A);
            _bPressed = state.IsPressed(GamepadButtonInputId.B);
            _xPressed = state.IsPressed(GamepadButtonInputId.X);
            _leftShoulderPressed = state.IsPressed(GamepadButtonInputId.LeftShoulder);
            _rightShoulderPressed = state.IsPressed(GamepadButtonInputId.RightShoulder);
        }

        private void ResetDirections()
        {
            _up.IsPressed = false;
            _down.IsPressed = false;
            _left.IsPressed = false;
            _right.IsPressed = false;
        }

        private void ResetState()
        {
            _aPressed = false;
            _bPressed = false;
            _xPressed = false;
            _leftShoulderPressed = false;
            _rightShoulderPressed = false;
            ResetDirections();
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= Poll;
            _driver.OnGamepadConnected -= OnGamepadChanged;
            _driver.OnGamepadDisconnected -= OnGamepadChanged;
            _gamepad?.Dispose();
            _gamepad = null;
        }

        private sealed class RepeatState
        {
            public bool IsPressed;
            public DateTime NextRepeat;
        }
    }
}
