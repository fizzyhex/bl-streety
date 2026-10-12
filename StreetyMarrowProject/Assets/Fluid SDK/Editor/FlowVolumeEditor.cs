using UnityEditor;

using UnityEngine;

namespace FluidLab
{
    [CustomEditor(typeof(FlowVolume))]
    public class FlowVolumeEditor : Editor
    {
        public void OnSceneGUI()
        {
            var volume = target as FlowVolume;

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
            var volume = target as FlowVolume;

            base.OnInspectorGUI();

            if (volume.TryGetComponent<BoxCollider>(out var boxCollider))
            {
                ShowBoxButton(volume, boxCollider);
            }

            var collider = volume.GetComponent<Collider>();

            if (!collider)
            {
                EditorGUILayout.HelpBox("This Flow Volume is missing a trigger collider! Please add one!", MessageType.Error);
            }
            else if (!collider.isTrigger)
            {
                EditorGUILayout.HelpBox("This Flow Volume's collider is not a trigger collider! Please enable \"Is Trigger\"!", MessageType.Error);
            }
        }

        private void ShowBoxButton(FlowVolume volume, BoxCollider boxCollider)
        {
            if (GUILayout.Button("Copy From Box Collider"))
            {
                Undo.RecordObject(volume, "Copy Box Collider To Flow Volume");

                volume.center = boxCollider.center;
                volume.size = boxCollider.size;
            }
        }
    }
}