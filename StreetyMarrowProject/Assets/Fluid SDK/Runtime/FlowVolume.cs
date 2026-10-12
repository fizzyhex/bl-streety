#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;

namespace FluidLab
{
    [AddComponentMenu("Fluids/Flow Volume")]
    public class FlowVolume : MonoBehaviour
    {
        [Tooltip("The center of the flow bounds.")]
        public Vector3 center = Vector3.zero;

        [Tooltip("The size of the flow bounds.")]
        public Vector3 size = Vector3.one;

        [Tooltip("The additional flow applied to the fluid in m/s, relative to this volume.")]
        public Vector3 flow = Vector3.zero;

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;

            Gizmos.color = Color.green;

            Gizmos.DrawWireCube(center, size);
        }

        [MenuItem("GameObject/Fluids/Flow Volume", priority = 1)]
        private static void MenuCreateVolume(MenuCommand menuCommand)
        {
            GameObject go = new("Flow Volume", typeof(FlowVolume));
            go.transform.localScale = Vector3.one;

            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Selection.activeObject = go;

            go.AddComponent<BoxCollider>().isTrigger = true;

            Undo.RegisterCreatedObjectUndo(go, "Create Flow Volume");
        }
#endif
    }
}