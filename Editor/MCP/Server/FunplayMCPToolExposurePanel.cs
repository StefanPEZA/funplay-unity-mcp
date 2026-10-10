// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.Collections.Generic;
using Funplay.Editor.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Funplay.Editor.MCP.Server
{
    internal sealed class FunplayMCPToolExposurePanel
    {
        private readonly ISettingsController _settings;
        private readonly Action _refreshStatus;

        public FunplayMCPToolExposurePanel(ISettingsController settings, Action refreshStatus)
        {
            _settings = settings;
            _refreshStatus = refreshStatus;
        }

        public void AddTo(VisualElement parent)
        {
            var toolProfileChoices = new List<string> { "core", "full" };
            var currentProfile = _settings.MCPToolExportProfile ?? "core";
            var toolProfileField = new PopupField<string>(
                "Tool Exposure",
                toolProfileChoices,
                Mathf.Max(0, toolProfileChoices.IndexOf(currentProfile)));
            toolProfileField.SetValueWithoutNotify(currentProfile);
            toolProfileField.RegisterValueChangedCallback(evt =>
            {
                _settings.MCPToolExportProfile = evt.newValue;
                _refreshStatus?.Invoke();
            });
            toolProfileField.style.flexGrow = 1;
            toolProfileField.style.flexShrink = 1;
            toolProfileField.style.minWidth = 0;
            toolProfileField.style.marginBottom = 0;

            // Gear button opening the full Tool Exposure settings window (per-tool checklist),
            // so the row's core/full quick-switch also offers one-click access to fine-grained control.
            // Pre-select the currently-active profile as the list to edit (read fresh on click), so
            // switching the dropdown to "full" and opening the settings edits the full list, not core.
            var settingsButton = new Button(() => FunplayToolExposureWindow.ShowWindow(_settings.MCPToolExportProfile))
            {
                tooltip = "Open the Tool Exposure settings window"
            };
            var gearIcon = EditorGUIUtility.IconContent(EditorGUIUtility.isProSkin ? "d_SettingsIcon" : "SettingsIcon");
            if (gearIcon != null && gearIcon.image != null)
            {
                settingsButton.style.backgroundImage = new StyleBackground((Texture2D)gearIcon.image);
            }
            else
            {
                // Fallback if the built-in icon can't be resolved on this editor version/skin.
                settingsButton.text = "\u2699";
            }
            settingsButton.style.width = 22;
            settingsButton.style.minWidth = 22;
            settingsButton.style.maxWidth = 22;
            settingsButton.style.height = 20;
            settingsButton.style.flexShrink = 0;
            settingsButton.style.marginLeft = 4;
            settingsButton.style.marginTop = 0;
            settingsButton.style.marginBottom = 0;

            // Row: the core/full dropdown grows to fill, the gear button sits on its right.
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 4;
            row.Add(toolProfileField);
            row.Add(settingsButton);
            parent.Add(row);

            parent.Add(BuildUiPackRow());

            var hint = new Label("core reduces tool-list noise for AI clients. full exposes every tool. The UI pack picks which UI framework's tools Core includes.");
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.fontSize = 10;
            hint.style.color = new Color(0.65f, 0.65f, 0.65f);
            hint.style.marginBottom = 10;
            parent.Add(hint);
        }

        private VisualElement BuildUiPackRow()
        {
            var choices = new List<string> { "auto", "ugui", "uitk", "both" };
            var current = string.IsNullOrEmpty(_settings.MCPUiPack) ? "auto" : _settings.MCPUiPack;
            var field = new PopupField<string>("UI Pack", choices, Mathf.Max(0, choices.IndexOf(current)), FormatChoice, FormatChoice);
            field.RegisterValueChangedCallback(evt =>
            {
                _settings.MCPUiPack = evt.newValue;
                _refreshStatus?.Invoke();
            });
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 0;
            field.style.marginBottom = 0;

            var redetect = new Button(() =>
            {
                MCPUiFrameworkDetector.Detect(_settings);
                _refreshStatus?.Invoke();
            })
            {
                text = "Re-detect",
                tooltip = "Scan Assets/ again for UI Toolkit (PanelSettings, UXML, USS) and uGUI (Canvas in scenes/prefabs)."
            };
            redetect.style.marginLeft = 4;
            redetect.style.height = 20;

            // Keep the "auto (detected: …)" label current when detection finishes.
            Action relabel = () => field.SetValueWithoutNotify(field.value);
            field.RegisterCallback<AttachToPanelEvent>(_ => _settings.OnSettingsChanged += relabel);
            field.RegisterCallback<DetachFromPanelEvent>(_ => _settings.OnSettingsChanged -= relabel);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 4;
            row.Add(field);
            row.Add(redetect);
            return row;
        }

        private string FormatChoice(string choice)
        {
            switch (choice)
            {
                case "auto":
                    var detected = _settings.MCPDetectedUiPack;
                    return "auto (detected: " + (string.IsNullOrEmpty(detected) ? "pending" : Describe(detected)) + ")";
                default:
                    return Describe(choice);
            }
        }

        private static string Describe(string pack)
        {
            switch (pack)
            {
                case "ugui": return "uGUI";
                case "uitk": return "UI Toolkit";
                default: return "uGUI + UI Toolkit";
            }
        }
    }
}
