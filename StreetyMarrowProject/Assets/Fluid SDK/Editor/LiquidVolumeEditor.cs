using System;
using UltEvents;
using UnityEditor;

using UnityEngine;

namespace FluidLab
{
    [CustomEditor(typeof(LiquidVolume))]
    public class LiquidVolumeEditor : Editor
    {
        private SerializedProperty _density;
        private SerializedProperty _center;
        private SerializedProperty _size;
        private SerializedProperty _flow;

        private SerializedProperty _ambienceBarcode;
        private SerializedProperty _ambienceEnabled;

        private SerializedProperty _splashVfxBarcode;
        private SerializedProperty _splashSfxBarcode;
        private SerializedProperty _splashEnabled;

        private void OnEnable()
        {
            _density = serializedObject.FindProperty(nameof(LiquidVolume.density));
            _center = serializedObject.FindProperty(nameof(LiquidVolume.center));
            _size = serializedObject.FindProperty(nameof(LiquidVolume.size));
            _flow = serializedObject.FindProperty(nameof(LiquidVolume.flow));

            _ambienceBarcode = serializedObject.FindProperty(nameof(LiquidVolume.ambienceBarcode));
            _ambienceEnabled = serializedObject.FindProperty(nameof(LiquidVolume.ambienceEnabled));

            _splashVfxBarcode = serializedObject.FindProperty(nameof(LiquidVolume.splashVfxBarcode));
            _splashSfxBarcode = serializedObject.FindProperty(nameof(LiquidVolume.splashSfxBarcode));
            _splashEnabled = serializedObject.FindProperty(nameof(LiquidVolume.splashEnabled));
        }

        public void OnSceneGUI()
        {
            var volume = target as LiquidVolume;

            var flow = volume.transform.TransformDirection(volume.flow);

            if (flow.sqrMagnitude <= 0.01f)
            {
                return;
            }

            var position = volume.transform.position;
            var rotation = Quaternion.LookRotation(flow);

            var end = position + flow;

            Handles.color = Color.cyan;

            Handles.DrawLine(position, end, 0.1f);

            Handles.ConeHandleCap(0, end, rotation, 0.12f, EventType.Repaint);
        }

        public override void OnInspectorGUI()
        {
            var volume = target as LiquidVolume;

            var lifeCycleEvents = volume.GetComponent<LifeCycleEvents>();

            if (!lifeCycleEvents)
            {
                EditorGUILayout.HelpBox("This Liquid Volume needs a LifeCycleEvents in order to store its parameters! Please add one!", MessageType.Warning);

                if (GUILayout.Button("Add LifeCycleEvents"))
                {
                    var newEvents = volume.gameObject.AddComponent<LifeCycleEvents>();

                    ApplyUltEvents(volume, newEvents);

                    Undo.RegisterCreatedObjectUndo(newEvents, "Create LifeCycleEvents");
                }

                return;
            }

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.PropertyField(_density);
            EditorGUILayout.PropertyField(_center);
            EditorGUILayout.PropertyField(_size);
            EditorGUILayout.PropertyField(_flow);

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
            }

            if (volume.TryGetComponent<BoxCollider>(out var boxCollider))
            {
                ShowBoxButton(volume, boxCollider);
            }

            GUILayout.Label("Common Densities", EditorStyles.whiteLargeLabel);

            if (GUILayout.Button("Set Water Density"))
            {
                Undo.RecordObject(volume, "Set Water Density");

                volume.density = 1000f;
            }

            if (GUILayout.Button("Set Lava Density"))
            {
                Undo.RecordObject(volume, "Set Lava Density");

                volume.density = 3100f;
            }

            if (GUILayout.Button("Set Acid Density"))
            {
                Undo.RecordObject(volume, "Set Acid Density");

                volume.density = 1180f;
            }

            GUILayout.Label("Effects", EditorStyles.whiteLargeLabel);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.PropertyField(_ambienceBarcode);
            EditorGUILayout.PropertyField(_ambienceEnabled);

            EditorGUILayout.PropertyField(_splashVfxBarcode);
            EditorGUILayout.PropertyField(_splashSfxBarcode);
            EditorGUILayout.PropertyField(_splashEnabled);

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();

                ApplyUltEvents(volume, lifeCycleEvents);
            }

            var collider = volume.GetComponent<Collider>();

            if (!collider)
            {
                EditorGUILayout.HelpBox("This Liquid Volume is missing a trigger collider! Please add one!", MessageType.Error);
            }
            else if (!collider.isTrigger)
            {
                EditorGUILayout.HelpBox("This Liquid Volume's collider is not a trigger collider! Please enable \"Is Trigger\"!", MessageType.Error);
            }
        }

        private void ShowBoxButton(LiquidVolume volume, BoxCollider boxCollider)
        {
            if (GUILayout.Button("Copy From Box Collider"))
            {
                Undo.RecordObject(volume, "Copy Box Collider To Liquid Volume");

                volume.center = boxCollider.center;
                volume.size = boxCollider.size;
            }
        }

        private void ApplyUltEvents(LiquidVolume volume, LifeCycleEvents lifeCycleEvents)
        {
            var awakeEvent = new UltEvent();

            if (!string.IsNullOrWhiteSpace(volume.ambienceBarcode))
            {
                AddStringCall(volume.SetAmbience, volume.ambienceBarcode, awakeEvent);
            }

            if (!volume.ambienceEnabled)
            {
                AddBoolCall(volume.ToggleAmbience, volume.ambienceEnabled, awakeEvent);
            }

            if (!string.IsNullOrWhiteSpace(volume.splashVfxBarcode))
            {
                AddStringCall(volume.SetSplashVFX, volume.splashVfxBarcode, awakeEvent);
            }

            if (!string.IsNullOrWhiteSpace(volume.splashSfxBarcode))
            {
                AddStringCall(volume.SetSplashSFX, volume.splashSfxBarcode, awakeEvent);
            }

            if (!volume.splashEnabled)
            {
                AddBoolCall(volume.ToggleSplash, volume.splashEnabled, awakeEvent);
            }

            lifeCycleEvents.AwakeEvent = awakeEvent;

            EditorUtility.SetDirty(lifeCycleEvents);
        }

        private void AddStringCall(Action<string> action, string value, UltEvent ultEvent)
        {
            var call = ultEvent.AddPersistentCall(action);

            call.SetArguments(value);
        }

        private void AddBoolCall(Action<bool> action, bool value, UltEvent ultEvent)
        {
            var call = ultEvent.AddPersistentCall(action);

            call.SetArguments(value);
        }
    }
}