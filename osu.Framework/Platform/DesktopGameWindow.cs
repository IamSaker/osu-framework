﻿// Copyright (c) 2007-2018 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu-framework/master/LICENCE

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using osu.Framework.Configuration;
using osu.Framework.Input;
using OpenTK;
using OpenTK.Graphics;

namespace osu.Framework.Platform
{
    public abstract class DesktopGameWindow : GameWindow
    {
        private const int default_width = 1366;
        private const int default_height = 768;

        private readonly BindableSize sizeFullscreen = new BindableSize();
        private readonly BindableSize sizeWindowed = new BindableSize();

        private readonly BindableDouble windowPositionX = new BindableDouble();
        private readonly BindableDouble windowPositionY = new BindableDouble();

        private DisplayDevice lastFullscreenDisplay;
        private bool inWindowModeTransition;

        public readonly Bindable<WindowMode> WindowMode = new Bindable<WindowMode>();

        public readonly Bindable<ConfineMouseMode> ConfineMouseMode = new Bindable<ConfineMouseMode>();

        internal override IGraphicsContext Context => Implementation.Context;

        protected new OpenTK.GameWindow Implementation => (OpenTK.GameWindow)base.Implementation;

        public readonly BindableBool MapAbsoluteInputToWindow = new BindableBool();

        public override DisplayDevice GetCurrentDisplay() => DisplayDevice.FromRectangle(Bounds) ?? DisplayDevice.Default;

        public override IEnumerable<DisplayResolution> AvailableResolutions => GetCurrentDisplay().AvailableResolutions;

        protected DesktopGameWindow()
            : base(default_width, default_height)
        {
            Resize += OnResize;
            Move += OnMove;
        }

        public virtual void SetIconFromStream(Stream stream)
        {
        }

        public override void SetupWindow(FrameworkConfigManager config)
        {
            config.BindWith(FrameworkSetting.SizeFullscreen, sizeFullscreen);

            sizeFullscreen.ValueChanged += newSize =>
            {
                if (WindowState == WindowState.Fullscreen)
                    ChangeResolution(GetCurrentDisplay(), newSize);
            };

            config.BindWith(FrameworkSetting.WindowedSize, sizeWindowed);

            config.BindWith(FrameworkSetting.WindowedPositionX, windowPositionX);
            config.BindWith(FrameworkSetting.WindowedPositionY, windowPositionY);

            config.BindWith(FrameworkSetting.ConfineMouseMode, ConfineMouseMode);

            config.BindWith(FrameworkSetting.MapAbsoluteInputToWindow, MapAbsoluteInputToWindow);

            ConfineMouseMode.ValueChanged += confineMouseMode_ValueChanged;
            ConfineMouseMode.TriggerChange();

            config.BindWith(FrameworkSetting.WindowMode, WindowMode);

            WindowMode.ValueChanged += windowMode_ValueChanged;
            WindowMode.TriggerChange();

            Exited += onExit;
        }

        protected virtual void ChangeResolution(DisplayDevice display, Size newSize)
        {
            if (newSize.Width == display.Width && newSize.Height == display.Height)
                return;

            var newResolution = display.AvailableResolutions
                                              .Where(r => r.Width == newSize.Width && r.Height == newSize.Height)
                                              .OrderByDescending(r => r.RefreshRate)
                                              .FirstOrDefault();

            if (newResolution == null)
            {
                // we wanted a new resolution but got nothing, which means OpenTK didn't find this resolution
                RestoreResolution(display);
            }
            else
            {
                display.ChangeResolution(newResolution);
                ClientSize = newSize;
            }
        }

        protected virtual void RestoreResolution(DisplayDevice displayDevice) => displayDevice.RestoreResolution();

        protected void OnResize(object sender, EventArgs e)
        {
            if (ClientSize.IsEmpty) return;

            switch (WindowMode.Value)
            {
                case Configuration.WindowMode.Windowed:
                    sizeWindowed.Value = ClientSize;
                    break;
            }
        }

        protected void OnMove(object sender, EventArgs e)
        {
            if (inWindowModeTransition) return;
            if (WindowMode.Value == Configuration.WindowMode.Windowed)
            {
                // Values are clamped to a range of [-0.5, 1.5], so if more than half of the window was
                // outside of the combined screen area before the game was closed, it will be moved so
                // that at least half of it is on screen after a restart.
                windowPositionX.Value = Position.X;
                windowPositionY.Value = Position.Y;
            }
        }

        private void confineMouseMode_ValueChanged(ConfineMouseMode newValue)
        {
            bool confine = false;

            switch (newValue)
            {
                case Input.ConfineMouseMode.Fullscreen:
                    confine = WindowMode.Value != Configuration.WindowMode.Windowed;
                    break;
                case Input.ConfineMouseMode.Always:
                    confine = true;
                    break;
            }

            if (confine)
                CursorState |= CursorState.Confined;
            else
                CursorState &= ~CursorState.Confined;
        }

        private void windowMode_ValueChanged(WindowMode newMode) => UpdateWindowMode(newMode);

        protected virtual void UpdateWindowMode(WindowMode newMode)
        {
            var currentDisplay = GetCurrentDisplay();

            try
            {
                inWindowModeTransition = true;
                switch (newMode)
                {
                    case Configuration.WindowMode.Fullscreen:
                        ChangeResolution(currentDisplay, sizeFullscreen);
                        lastFullscreenDisplay = currentDisplay;

                        WindowState = WindowState.Fullscreen;
                        break;
                    case Configuration.WindowMode.Borderless:
                        if (lastFullscreenDisplay != null)
                            RestoreResolution(lastFullscreenDisplay);
                        lastFullscreenDisplay = null;

                        WindowState = WindowState.Maximized;
                        WindowBorder = WindowBorder.Hidden;

                        //must add 1 to enter borderless
                        ClientSize = new Size(currentDisplay.Bounds.Width + 1, currentDisplay.Bounds.Height + 1);
                        break;
                    case Configuration.WindowMode.Windowed:
                        if (lastFullscreenDisplay != null)
                            RestoreResolution(lastFullscreenDisplay);
                        lastFullscreenDisplay = null;

                        WindowState = WindowState.Normal;
                        WindowBorder = WindowBorder.Resizable;

                        ClientSize = sizeWindowed;
                        Position = new Vector2((float)windowPositionX, (float)windowPositionY);
                        break;
                }
            }
            finally {
                inWindowModeTransition = false;
            }

            ConfineMouseMode.TriggerChange();
        }

        private void onExit()
        {
            switch (WindowMode.Value)
            {
                case Configuration.WindowMode.Fullscreen:
                    sizeFullscreen.Value = ClientSize;
                    break;
            }

            if (lastFullscreenDisplay != null)
                RestoreResolution(lastFullscreenDisplay);
            lastFullscreenDisplay = null;
        }

        public Vector2 Position
        {
            get
            {
                var display = GetCurrentDisplay();
                var relativeLocation = new Point(Location.X - display.Bounds.X, Location.Y - display.Bounds.Y);

                return new Vector2(
                    display.Width  > Size.Width  ? (float)relativeLocation.X / (display.Width  - Size.Width)  : 0,
                    display.Height > Size.Height ? (float)relativeLocation.Y / (display.Height - Size.Height) : 0);
            }
            set
            {
                var display = GetCurrentDisplay();

                var relativeLocation = new Point(
                    (int)Math.Round((display.Width - Size.Width) * value.X),
                    (int)Math.Round((display.Height - Size.Height) * value.Y));

                Location = new Point(relativeLocation.X + display.Bounds.X, relativeLocation.Y + display.Bounds.Y);
            }
        }

        public override void CycleMode()
        {
            switch (WindowMode.Value)
            {
                case Configuration.WindowMode.Windowed:
                    WindowMode.Value = Configuration.WindowMode.Borderless;
                    break;
                case Configuration.WindowMode.Borderless:
                    WindowMode.Value = Configuration.WindowMode.Fullscreen;
                    break;
                default:
                    WindowMode.Value = Configuration.WindowMode.Windowed;
                    break;
            }
        }

        public override VSyncMode VSync
        {
            get => Implementation.VSync;
            set => Implementation.VSync = value;
        }
    }
}
