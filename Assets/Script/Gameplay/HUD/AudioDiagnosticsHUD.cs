using System;
using UnityEngine;
using TMPro;
using YARG.Audio.BASS;
using YARG.Core.Audio;
using YARG.Settings;

namespace YARG.Gameplay.HUD
{
    public class AudioDiagnosticsHUD : GameplayBehaviour
    {
        private const string DRAGGABLE_ELEMENT_NAME = "AudioDiagnostics";
        
        [SerializeField] private GameObject _container;
        [SerializeField] private TextMeshProUGUI _diagnosticsText;
        
        private DraggableHudElement _draggableElement;
        private AudioDiagnosticsCollector _diagnosticsCollector;
        private float _lastUpdateTime;
        private const float UPDATE_INTERVAL = 0.5f; // Update every 500ms
        
        protected override void GameplayAwake()
        {
            // Set up draggable element
            _draggableElement = GetComponent<DraggableHudElement>();
            if (_draggableElement == null)
            {
                _draggableElement = gameObject.AddComponent<DraggableHudElement>();
            }
            
            // Initialize diagnostics collector
            InitializeDiagnosticsCollector();
            
            // Set initial visibility based on settings
            UpdateVisibility();
        }

        protected override void GameplayDestroy()
        {
            // Nothing to clean up currently
        }

        private void Update()
        {
            // Check if visibility setting has changed
            UpdateVisibility();
            
            if (!gameObject.activeInHierarchy || _container == null || !_container.activeInHierarchy)
                return;

            // Update diagnostics at regular intervals
            if (Time.unscaledTime - _lastUpdateTime >= UPDATE_INTERVAL)
            {
                UpdateDiagnosticsDisplay();
                _lastUpdateTime = Time.unscaledTime;
            }
        }

        private void InitializeDiagnosticsCollector()
        {
            try
            {
                // Note: This requires a way to access the BassAudioManager instance
                // For now, this will be null and the diagnostics will show unavailable
                _diagnosticsCollector = null;
                Debug.LogWarning("AudioDiagnosticsCollector requires BassAudioManager instance - diagnostics unavailable");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to initialize AudioDiagnosticsCollector: {ex}");
            }
        }

        private void UpdateDiagnosticsDisplay()
        {
            if (_diagnosticsCollector == null || _diagnosticsText == null)
            {
                if (_diagnosticsText != null)
                {
                    _diagnosticsText.text = "Audio diagnostics unavailable";
                }
                return;
            }

            try
            {
                var diagnostics = _diagnosticsCollector.GetCurrentDiagnostics();
                if (diagnostics != null)
                {
                    // Format the diagnostic information for display
                    string displayText = FormatDiagnosticsText(diagnostics);
                    _diagnosticsText.text = displayText;
                    
                    // Update text color based on connection status
                    if (diagnostics.HasErrors)
                    {
                        _diagnosticsText.color = Color.red;
                    }
                    else if (!diagnostics.IsConnected)
                    {
                        _diagnosticsText.color = Color.yellow;
                    }
                    else
                    {
                        _diagnosticsText.color = Color.white;
                    }
                }
            }
            catch (Exception ex)
            {
                _diagnosticsText.text = $"Error: {ex.Message}";
                _diagnosticsText.color = Color.red;
            }
        }

        private string FormatDiagnosticsText(AudioDiagnosticsInfo diagnostics)
        {
            var status = diagnostics.IsConnected ? "Connected" : "Disconnected";
            var statusColor = diagnostics.IsConnected ? "#00FF00" : "#FFFF00";
            
            if (diagnostics.HasErrors)
            {
                status = "Error";
                statusColor = "#FF0000";
            }

            return $"<b>Audio Diagnostics</b>\n" +
                   $"<color={statusColor}>{status}</color>\n" +
                   $"<color=#CCCCCC>Backend:</color> {diagnostics.BackendType}\n" +
                   $"<color=#CCCCCC>Device:</color> {diagnostics.DeviceName}\n" +
                   $"<color=#CCCCCC>Format:</color> {diagnostics.SampleRate}Hz, {diagnostics.Channels}ch\n" +
                   $"<color=#CCCCCC>Latency:</color> {diagnostics.LatencyMs:F1}ms\n" +
                   $"<color=#888888>{diagnostics.LastUpdated:HH:mm:ss}</color>";
        }


        private void UpdateVisibility()
        {
            bool shouldShow = SettingsManager.Settings.ShowAudioDiagnosticsPanel?.Value ?? false;
            
            if (_container != null)
            {
                _container.SetActive(shouldShow);
            }
            else
            {
                gameObject.SetActive(shouldShow);
            }
        }
    }
}