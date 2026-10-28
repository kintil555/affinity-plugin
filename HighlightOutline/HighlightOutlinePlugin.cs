using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HarmonyLib;
using AffinityPluginLoader;

namespace HighlightOutline
{
    public class HighlightOutlinePlugin : AffinityPlugin
    {
        internal static IPluginContext Ctx;
        internal static bool SuppressRasteriseDialog;

        const string PersonaAsm = "Serif.Interop.Persona";
        const string AffinityAsm = "Serif.Affinity";

        public override void OnPatch(Harmony harmony, IPluginContext context)
        {
            Ctx = context;

            // Right-click menu (AffinityContextMenu): remove our items before UpdateState, add after
            context.Patch("Layer context menu", h =>
            {
                var t = Asm(AffinityAsm).GetType("Serif.Affinity.UI.AffinityContextMenu", true);
                var m = t.GetMethod("OnOpened", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                h.Patch(m, prefix: new HarmonyMethod(typeof(HighlightOutlinePlugin), nameof(OnOpened_Prefix)));
                context.Log("Patched AffinityContextMenu.OnOpened");
            });

            // Skip the rasterise dialog (Preserve Layer FX = off) when we trigger it
            context.Patch("Rasterise dialog", h =>
            {
                var t = Asm(AffinityAsm).GetType("Serif.Affinity.Services.UserInterfaceService", true);
                var m = t.GetMethod("ShowRasteriseDialog", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                h.Patch(m, prefix: new HarmonyMethod(typeof(HighlightOutlinePlugin), nameof(ShowRasteriseDialog_Prefix)));
            });
        }

        // bool ShowRasteriseDialog(ref bool preserveLayerFx, ref int resample, bool fxEnabled, bool resamplingEnabled)
        public static bool ShowRasteriseDialog_Prefix(ref bool __0, ref bool __result)
        {
            if (!SuppressRasteriseDialog) return true;
            __0 = false;        // do not preserve layer FX
            __result = true;    // dialog "accepted" (assumed true = OK)
            return false;
        }

        static object _toolsItem;   // our WorkspaceMenuItem (reused)
        static int _diagLogged;

        // The layer menu's ItemsSource is a ReadOnlyCollection (LayerView.TryMakeMenu uses List.AsReadOnly()),
        // so it cannot be modified in place. Copy it into a new List, insert our item, and swap ItemsSource.
        // UpdateState (runs in base OnOpened path) casts each item to WorkspaceMenuItem, so we use a real one.
        public static void OnOpened_Prefix(ContextMenu __instance)
        {
            try
            {
                var wmiType = Asm(AffinityAsm).GetType("Serif.Affinity.Workspaces.WorkspaceMenuItem", true);
                var src = (__instance.ItemsSource as IEnumerable) ?? __instance.Items;
                var items = src.Cast<object>().Where(o => o != null).ToList();
                if (items.Contains(_toolsItem)) return;

                var textProp = wmiType.GetProperty("Text");
                var cmdProp = wmiType.GetProperty("Command");
                int dup = items.FindIndex(o =>
                {
                    if (!wmiType.IsInstanceOfType(o)) return false;
                    var cmd = cmdProp.GetValue(o);
                    return (cmd != null && cmd.GetType().Name == "DuplicateObjectsIgnorePixelSelectionCommand")
                        || (string)textProp.GetValue(o) == "Duplicate";
                });
                if (dup < 0)
                {
                    if (_diagLogged++ < 3)
                        Ctx?.Log("Menu not matched. Items: " + string.Join(" | ",
                            items.Select(o => wmiType.IsInstanceOfType(o) ? (string)textProp.GetValue(o) ?? "<null>" : o.GetType().Name)));
                    return;
                }

                if (_toolsItem == null) _toolsItem = BuildToolsItem(wmiType);

                var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(wmiType);
                var newList = (IList)Activator.CreateInstance(listType);
                for (int i = 0; i < items.Count; i++)
                {
                    newList.Add(items[i]);
                    if (i == dup) newList.Add(_toolsItem);
                }
                __instance.ItemsSource = newList;
            }
            catch (Exception ex) { Ctx?.LogError("Context menu patch failed", ex); }
        }

        static object BuildToolsItem(Type wmiType)
        {
            var cmdType = PType("Serif.Interop.Persona.Commands.Command");
            var cmd = Activator.CreateInstance(MakeCommandType(cmdType));

            var child = wmiType.GetConstructor(new[] { typeof(string), cmdType }).Invoke(new[] { "Highlight Outline", cmd });
            var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(wmiType);
            var children = (IList)Activator.CreateInstance(listType);
            children.Add(child);
            var ilist = typeof(System.Collections.Generic.IList<>).MakeGenericType(wmiType);
            return wmiType.GetConstructor(new[] { typeof(string), ilist }).Invoke(new object[] { "Tools", children });
        }

        // Runtime subclass of Serif's abstract Command: Execute opens our panel.
        static Type MakeCommandType(Type baseType)
        {
            var ab = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("HighlightOutline.Dyn"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var mb = ab.DefineDynamicModule("m");
            var tb = mb.DefineType("HighlightOutlineCommand", TypeAttributes.Public | TypeAttributes.Class, baseType);

            var baseCtor = baseType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
            var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var il = ctor.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Call, baseCtor);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);

            var attrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig;
            var exec = tb.DefineMethod("Execute", attrs, typeof(void), new[] { typeof(object) });
            il = exec.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(HighlightOutlinePlugin).GetMethod(nameof(OpenPanel), BindingFlags.Public | BindingFlags.Static));
            il.Emit(System.Reflection.Emit.OpCodes.Ret);

            var can = tb.DefineMethod("CanExecute", attrs, typeof(bool), new[] { typeof(object) });
            il = can.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);

            return tb.CreateType();
        }

        static Window _panel;

        // Opens our compact panel (Affinity-styled controls) on the current selection.
        public static void OpenPanel()
        {
            try
            {
                if (_panel != null) { _panel.Activate(); return; }
                _panel = HighlightPanel.Build(RasteriseSelection);
                _panel.Closed += (s, e) => _panel = null;
                _panel.Show();
            }
            catch (Exception ex) { _panel = null; Ctx?.LogError("Open panel failed", ex); }
        }

        // Rasterise with "Preserve Layer FX" off so the FX become plain pixels.
        internal static void RasteriseSelection()
        {
            try
            {
                var t = PType("Serif.Interop.Persona.Commands.RasteriseCommand");
                var cmd = Activator.CreateInstance(t);
                if (!(bool)t.GetMethod("CanExecute").Invoke(cmd, new object[] { null }))
                { Ctx?.LogWarning("Rasterise not available for selection"); return; }

                SuppressRasteriseDialog = true;
                try { t.GetMethod("Execute").Invoke(cmd, new object[] { null }); }
                finally { SuppressRasteriseDialog = false; }
            }
            catch (Exception ex) { Ctx?.LogError("Rasterise failed", ex); }
        }

        internal static object GetService(Type serviceType)
        {
            var app = Application.Current;
            var m = app.GetType().GetMethods()
                .First(x => x.Name == "GetService" && x.IsGenericMethod && x.GetParameters().Length == 0)
                .MakeGenericMethod(serviceType);
            return m.Invoke(app, null);
        }

        internal static object GetSelection()
        {
            var viewSvc = GetService(PType("Serif.Interop.Persona.Services.IDocumentViewService"));
            var doc = viewSvc.GetType().GetProperty("CurrentDocument").GetValue(viewSvc);
            return doc.GetType().GetProperty("Selection").GetValue(doc);
        }

        internal static Type FindTypeAnywhere(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("Serif")))
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                var t = types.FirstOrDefault(x => x.Name == name);
                if (t != null) return t;
            }
            return null;
        }

        internal static Assembly Asm(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == name);

        internal static Type PType(string full) => Asm(PersonaAsm).GetType(full, true);
    }

    /// <summary>Values edited in the panel.</summary>
    internal sealed class FxModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        double _opacity = 50, _fill = 0, _radius = 0, _offset = 0, _intensity = 0, _angle = 315;

        public double Opacity { get { return _opacity; } set { Set(ref _opacity, value, "Opacity"); } }          // %
        public double FillOpacity { get { return _fill; } set { Set(ref _fill, value, "FillOpacity"); } }       // %
        public double Radius { get { return _radius; } set { Set(ref _radius, value, "Radius"); } }             // px
        public double Offset { get { return _offset; } set { Set(ref _offset, value, "Offset"); } }             // px
        public double Intensity { get { return _intensity; } set { Set(ref _intensity, value, "Intensity"); } } // %
        public double Angle { get { return _angle; } set { Set(ref _angle, value, "Angle"); } }                 // degrees

        void Set(ref double f, double v, string name)
        {
            if (f == v) return;
            f = v;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Drives Affinity's EffectsDataSource: Inner Shadow on the current selection, live preview.</summary>
    internal sealed class FxSession : IDisposable
    {
        readonly object _ds;
        readonly object _item;
        readonly Type _dsType;

        public FxSession()
        {
            var app = Application.Current;
            _dsType = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Data.EffectsDataSource");
            _ds = Activator.CreateInstance(_dsType, app, false);
            _dsType.GetMethod("Initialise").Invoke(_ds, new[] { HighlightOutlinePlugin.GetSelection() });
            _dsType.GetMethod("Activate").Invoke(_ds, null);

            var effects = ((IEnumerable)_dsType.GetProperty("Effects").GetValue(_ds)).Cast<object>().ToList();
            _item = effects.First(e => e.GetType().Name == "EffectsDataSourceItemInnerShadow");
            _dsType.GetProperty("CurrentEffect").SetValue(_ds, effects.IndexOf(_item));

            var enabled = (bool)ItemType.GetProperty("IsEnabled").GetValue(_item);
            if (!enabled) _dsType.GetMethod("EnableEffect").Invoke(_ds, new[] { _item });

            SetBlendNormal();
            HighlightOutlinePlugin.Ctx?.Log("Inner Shadow params: " + string.Join(", ", Params().Select(p => p.GetType().Name)));
        }

        static Type ItemType { get { return HighlightOutlinePlugin.PType("Serif.Interop.Persona.Data.IEffectsDataSourceItem"); } }

        System.Collections.Generic.List<object> Params() =>
            ((IEnumerable)ItemType.GetProperty("Params").GetValue(_item)).Cast<object>().ToList();

        // Blend mode defaults to Normal (enum type resolved at runtime from CurrentBlendMode).
        void SetBlendNormal()
        {
            try
            {
                foreach (var p in Params().Where(x => x.GetType().Name == "EffectsDataSourceItemBlendModesParam"))
                {
                    var prop = p.GetType().GetProperty("CurrentBlendMode");
                    var pt = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    if (!pt.IsEnum) continue;
                    var normal = Enum.Parse(pt, "Normal");
                    _dsType.GetMethod("BeginEditEffect").Invoke(_ds, new[] { p });
                    prop.SetValue(p, normal);
                    _dsType.GetMethod("ContinueEditEffect").Invoke(_ds, new[] { p });
                    _dsType.GetMethod("EndEditEffect").Invoke(_ds, new[] { p });
                }
            }
            catch (Exception ex) { HighlightOutlinePlugin.Ctx?.LogError("Set blend Normal failed", ex); }
        }

        /// <summary>The current Inner Shadow colour (Persona Colour object).</summary>
        public object GetColour()
        {
            var p = Params().FirstOrDefault(x => x.GetType().Name == "EffectsDataSourceItemColourParam");
            return p?.GetType().GetProperty("Value").GetValue(p);
        }

        public object GetColourProfile()
        {
            try { return _dsType.GetProperty("ColourProfile")?.GetValue(_ds); } catch { return null; }
        }

        public void SetColour(object colour) { SetAll("EffectsDataSourceItemColourParam", "Value", colour); }

        public void PushAll(FxModel m)
        {
            foreach (var n in new[] { "Opacity", "FillOpacity", "Radius", "Offset", "Intensity", "Angle" }) Push(n, m);
        }

        public void Push(string name, FxModel m)
        {
            try
            {
                switch (name)
                {
                    case "Opacity": SetAll("EffectsDataSourceItemOpacityParam", "Value", m.Opacity / 100.0); break;
                    case "FillOpacity": SetFillOpacity(m.FillOpacity); break;
                    case "Radius": SetAll("EffectsDataSourceItemRadiusParam", "Value", m.Radius); break;
                    case "Intensity": SetAll("EffectsDataSourceItemIntensityParam", "Value", m.Intensity / 100.0); break;
                    case "Offset":
                        SetAll("EffectsDataSourceItemOffsetAngleParam", "Value1", m.Offset);   // Value1 = offset (px)
                        SetAll("EffectsDataSourceItemOffsetParam", "Value1", m.Offset);
                        break;
                    case "Angle":
                        SetAll("EffectsDataSourceItemOffsetAngleParam", "Value2", m.Angle);    // Value2 = angle (degrees)
                        SetAll("EffectsDataSourceItemAngleParam", "Value1", m.Angle);
                        break;
                }
            }
            catch (Exception ex) { HighlightOutlinePlugin.Ctx?.LogError("Set '" + name + "' failed", ex); }
        }

        void SetFillOpacity(double pct)
        {
            var uv = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Units.UnitValue");
            var ut = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Units.UnitType");
            var ctor = uv.GetConstructors().First(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == 2 && ps[0].ParameterType == typeof(double) && ps[1].ParameterType == ut;
            });
            _dsType.GetProperty("FillOpacity").SetValue(_ds, ctor.Invoke(new object[] { pct, Enum.Parse(ut, "Percentage") }));
        }

        void SetAll(string paramType, string prop, object value)
        {
            foreach (var p in Params().Where(x => x.GetType().Name == paramType))
            {
                try
                {
                    _dsType.GetMethod("BeginEditEffect").Invoke(_ds, new[] { p });
                    p.GetType().GetProperty(prop).SetValue(p, value);
                    _dsType.GetMethod("ContinueEditEffect").Invoke(_ds, new[] { p });
                    _dsType.GetMethod("EndEditEffect").Invoke(_ds, new[] { p });
                }
                catch (Exception ex) { HighlightOutlinePlugin.Ctx?.LogError(paramType + "." + prop + " failed", ex); }
            }
        }

        public void Dispose()
        {
            try { _dsType.GetMethod("OnClose").Invoke(_ds, null); } catch { }
            try { _dsType.GetMethod("Deactivate").Invoke(_ds, null); } catch { }
            (_ds as IDisposable)?.Dispose();
        }
    }

    /// <summary>Compact panel built from Affinity's own controls (falls back to plain WPF ones).</summary>
    internal static class HighlightPanel
    {
        public static Window Build(Action rasterise)
        {
            var fx = new FxSession();
            var model = new FxModel();

            var win = CreateWindow();
            win.Title = "Highlight Outline";
            win.Width = 440;
            win.SizeToContent = SizeToContent.Height;
            win.ResizeMode = ResizeMode.NoResize;
            win.Topmost = true;
            if (Application.Current.MainWindow != null && Application.Current.MainWindow != win)
            {
                win.Owner = Application.Current.MainWindow;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            var grid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Same rows as the design: Opacity, Offset, Angle, Color (Radius/Fill/Intensity stay at fixed defaults).
            AddRow(grid, "Opacity:", Ui.NumberSlider(model, "Opacity", 0, 100, 0, "Percentage", true, fx.GetColour(), fx.GetColourProfile()), 10);
            AddRow(grid, "Offset:", Ui.NumberSlider(model, "Offset", 0, 200, 0, "Pixel", false), 14);
            AddRow(grid, "Angle:", Ui.AngleDial(model, "Angle"), 14);

            // A native popup button without a ColourProfile can throw while rendering and take the whole panel down.
            var profile = fx.GetColourProfile();
            var colour = profile != null ? Ui.ColourButton(fx.GetColour(), profile, fx.SetColour) : null;
            if (profile == null) HighlightOutlinePlugin.Ctx?.LogWarning("ColourProfile unavailable; colour button skipped");
            if (colour != null) AddRow(grid, "Color:", colour, 14);

            var apply = new Button { Content = "Apply", Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 5, 0, 5) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(apply, grid.RowDefinitions.Count - 1);
            Grid.SetColumnSpan(apply, 2);
            grid.Children.Add(apply);
            win.Content = grid;

            // Keep the panel alive if one native control throws during layout/render; log instead.
            win.Dispatcher.UnhandledException += (s, e) =>
            {
                HighlightOutlinePlugin.Ctx?.LogError("Panel UI exception", e.Exception);
                e.Handled = true;
            };

            bool applied = false;
            apply.Click += (s, e) => { applied = true; win.Close(); };
            win.Closed += (s, e) =>
            {
                fx.Dispose();                                   // commits the FX to the layer
                if (applied) win.Dispatcher.BeginInvoke(rasterise);   // FX -> pixels, Preserve Layer FX off
            };

            model.PropertyChanged += (s, e) => fx.Push(e.PropertyName, model);
            fx.PushAll(model);                                  // defaults: radius 0, offset 10, intensity 0, fill 0%
            return win;
        }

        static void AddRow(Grid grid, string label, FrameworkElement control, double top = 12)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = grid.RowDefinitions.Count - 1;
            var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, top, 8, 0) };
            Ui.StyleLabel(tb);
            control.Margin = new Thickness(0, top, 0, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(tb, row); Grid.SetRow(control, row); Grid.SetColumn(control, 1);
            grid.Children.Add(tb); grid.Children.Add(control);
        }

        // Affinity's own dialog chrome when available.
        static Window CreateWindow()
        {
            try
            {
                var t = HighlightOutlinePlugin.Asm("Serif.Affinity").GetType("Serif.Affinity.UI.AffinityDialog", true);
                return (Window)Activator.CreateInstance(t);
            }
            catch (Exception ex)
            {
                HighlightOutlinePlugin.Ctx?.LogWarning("AffinityDialog unavailable, using plain window: " + ex.Message);
                return new Window();
            }
        }
    }

    /// <summary>Affinity control factory (RangeSlider + UnitEditor, ColourPickerButton) with WPF fallbacks.</summary>
    internal static class Ui
    {
        static Type _range, _editor, _unitValue, _unitType;
        static bool _resolved;

        static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _range = HighlightOutlinePlugin.Asm("Serif.Affinity").GetType("Serif.Affinity.UI.Controls.RangeSlider");
                _editor = HighlightOutlinePlugin.Asm("Serif.Affinity").GetType("Serif.Affinity.UI.Units.UnitEditor");
                _unitValue = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Units.UnitValue");
                _unitType = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Units.UnitType");
            }
            catch (Exception ex) { HighlightOutlinePlugin.Ctx?.LogWarning("Serif controls not resolved: " + ex.Message); }
        }

        public static void StyleLabel(TextBlock tb)
        {
            try
            {
                var scheme = HighlightOutlinePlugin.Asm("Serif.Affinity").GetType("Serif.Affinity.Resources.Schemes.SchemeManager");
                var key = scheme?.GetProperty("Brush_LabelForeground", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (key != null) { tb.SetResourceReference(TextBlock.ForegroundProperty, key); return; }
            }
            catch { }
            tb.Foreground = System.Windows.Media.Brushes.Gainsboro;
        }

        // Key of a SchemeManager brush (Affinity uses the static property value as the resource key).
        static object SchemeKey(string name)
        {
            try
            {
                var t = HighlightOutlinePlugin.Asm("Serif.Affinity").GetType("Serif.Affinity.Resources.Schemes.SchemeManager");
                return t?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? t?.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? name;
            }
            catch { return name; }
        }

        // Affinity's own opacity-slider look: ColourSliderStyle + Brush_Checkerboard + AlphaGradientConverter(RangeImage).
        static void ApplyOpacityLook(Slider slider, Type rangeType, object colour, object colourProfile)
        {
            try
            {
                var style = Application.Current.TryFindResource("ColourSliderStyle") as Style;
                if (style != null) slider.Style = style;
                slider.SetResourceReference(Control.BackgroundProperty, SchemeKey("Brush_Checkerboard"));

                var rangesDp = (DependencyProperty)rangeType.GetField("RangesProperty", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (rangesDp != null)
                {
                    var conv = System.ComponentModel.TypeDescriptor.GetConverter(rangesDp.PropertyType);
                    if (conv.CanConvertFrom(typeof(string))) slider.SetValue(rangesDp, conv.ConvertFromString("100,1"));
                }

                // RangeImage (AlphaGradientConverter) is skipped: built without a bound ColourProfile it makes
                // ColoursSource.GenerateBuffer throw a NullReference during Arrange and kills the whole panel.
            }
            catch (Exception ex) { HighlightOutlinePlugin.Ctx?.LogWarning("Opacity look partially applied: " + ex.Message); }
        }

        /// <summary>Affinity RoundSlider (circular angle dial with degree readout).</summary>
        public static FrameworkElement AngleDial(object model, string prop)
        {
            try
            {
                var t = HighlightOutlinePlugin.Asm("Serif.Affinity").GetType("Serif.Affinity.UI.Controls.RoundSlider", true);
                var dial = (Control)Activator.CreateInstance(t);
                var dp = (DependencyProperty)t.GetField("AngleProperty", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                var b = new Binding(prop) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
                if (dp.PropertyType != typeof(double))
                {
                    Resolve();
                    b.Converter = new DoubleToUnitValue(_unitValue, Enum.Parse(_unitType, "Degree"));
                }
                dial.SetBinding(dp, b);
                SetDp(t, dial, "Reverse", true);   // Affinity shadow angle runs counter-clockwise
                SetDp(t, dial, "FullDegreeMode", true);
                SetDp(t, dial, "Precision", 0);
                dial.Width = 64; dial.Height = 64;
                dial.HorizontalAlignment = HorizontalAlignment.Left;
                return dial;
            }
            catch (Exception ex)
            {
                HighlightOutlinePlugin.Ctx?.LogWarning("RoundSlider failed, using slider: " + ex.Message);
                return NumberSlider(model, prop, 0, 360, 0, "Degree", false);
            }
        }

        public static FrameworkElement NumberSlider(object model, string prop, double min, double max, int precision, string unit = "Number", bool checker = false, object colour = null, object colourProfile = null)
        {
            Resolve();
            var binding = new Binding(prop) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
            try
            {
                if (_range == null || _editor == null || _unitValue == null) throw new InvalidOperationException("no Serif controls");
                var number = Enum.Parse(_unitType, unit);

                var slider = (Slider)Activator.CreateInstance(_range);
                slider.Minimum = min; slider.Maximum = max;
                slider.VerticalAlignment = VerticalAlignment.Center;
                if (checker) ApplyOpacityLook(slider, _range, colour, colourProfile);
                slider.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, binding);

                var editor = (Control)Activator.CreateInstance(_editor);
                SetDp(_editor, editor, "Precision", (int?)precision);
                _editor.GetProperty("OutputUnitType")?.SetValue(editor, number);
                editor.Width = 72;
                editor.Margin = new Thickness(12, 0, 0, 0);
                var dp = (DependencyProperty)_editor.GetField("UnitValueProperty", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                BindingOperations.SetBinding(editor, dp, new Binding(prop)
                {
                    Source = model,
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    Converter = new DoubleToUnitValue(_unitValue, number)
                });

                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(editor, 1);
                g.Children.Add(slider); g.Children.Add(editor);
                return g;
            }
            catch (Exception ex)
            {
                HighlightOutlinePlugin.Ctx?.LogWarning("Serif slider failed, using WPF slider: " + ex.Message);
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                var sl = new Slider { Minimum = min, Maximum = max };
                sl.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, binding);
                var box = new TextBox { Margin = new Thickness(6, 0, 0, 0) };
                box.SetBinding(TextBox.TextProperty, new Binding(prop)
                {
                    Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                    StringFormat = precision > 0 ? "F" + precision : "F0"
                });
                Grid.SetColumn(box, 1);
                g.Children.Add(sl); g.Children.Add(box);
                return g;
            }
        }

        /// <summary>Affinity's ColourEditorPopupButton (swatch + popup picker) bound to the effect colour; null if unavailable.</summary>
        public static FrameworkElement ColourButton(object initial, object colourProfile, Action<object> changed)
        {
            try
            {
                var t = HighlightOutlinePlugin.Asm("Serif.Affinity").GetType("Serif.Affinity.UI.Controls.ColourEditorPopupButton");
                if (t == null || initial == null) { HighlightOutlinePlugin.Ctx?.LogWarning("ColourEditorPopupButton not found"); return null; }
                var btn = (FrameworkElement)Activator.CreateInstance(t);

                var dp = (DependencyProperty)t.GetField("ColourProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
                if (dp == null) { HighlightOutlinePlugin.Ctx?.LogWarning("ColourEditorPopupButton has no Colour property"); return null; }

                var cpDp = t.GetField("ColourProfileProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as DependencyProperty;
                if (cpDp != null && colourProfile != null) btn.SetValue(cpDp, colourProfile);
                SetDp(t, btn, "IsPickerVisible", true);
                SetDp(t, btn, "IsNoneVisible", false);
                var anm = t.GetField("AlphaNoiseModeProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as DependencyProperty;
                if (anm != null) btn.SetValue(anm, Enum.Parse(anm.PropertyType, "None"));

                btn.SetValue(dp, initial);
                System.ComponentModel.DependencyPropertyDescriptor.FromProperty(dp, t)
                    .AddValueChanged(btn, (s, e) => changed(btn.GetValue(dp)));
                btn.Width = 170; btn.Height = 30; btn.HorizontalAlignment = HorizontalAlignment.Left;
                return btn;
            }
            catch (Exception ex) { HighlightOutlinePlugin.Ctx?.LogError("Colour button failed", ex); return null; }
        }

        static void SetDp(Type type, DependencyObject obj, string name, object value)
        {
            var f = type.GetField(name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (f != null && value != null) obj.SetValue((DependencyProperty)f.GetValue(null), value);
        }
    }

    internal sealed class DoubleToUnitValue : IValueConverter
    {
        readonly ConstructorInfo _ctor;
        readonly PropertyInfo _value;
        readonly Type _type;
        readonly object _unit;

        public DoubleToUnitValue(Type unitValue, object unit)
        {
            _type = unitValue; _unit = unit;
            _ctor = unitValue.GetConstructor(new[] { typeof(double), unit.GetType() });
            _value = unitValue.GetProperty("Value");
        }

        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) =>
            v is double d ? _ctor.Invoke(new object[] { d, _unit }) : null;

        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) =>
            v != null && _type.IsInstanceOfType(v) ? _value.GetValue(v) : 0.0;
    }
}
