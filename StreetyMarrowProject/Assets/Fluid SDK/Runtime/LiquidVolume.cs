#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityEngine;

namespace FluidLab
{
    [AddComponentMenu("Fluids/Liquid Volume")]
    public class LiquidVolume : MonoBehaviour
    {
        [Min(0.001f)]
        [Tooltip("The density of the liquid. Defaults to 1000, the density of water. Measured in kg/m^3.")]
        public float density = 1000f;

        [Tooltip("The center of the liquid bounds.")]
        public Vector3 center = Vector3.zero;

        [Tooltip("The size of the liquid bounds.")]
        public Vector3 size = Vector3.one;

        [Tooltip("The local default amount of water flow, in m/s.")]
        public Vector3 flow = Vector3.zero;

        [Tooltip("The MonoDisc barcode of the ambience. If empty, uses the default underwater ambience.")]
        public string ambienceBarcode = string.Empty;

        [Tooltip("Is the ambience track enabled?")]
        public bool ambienceEnabled = true;

        [Tooltip("The spawnable barcode of the splash VFX. If empty, uses the default water splash.")]
        public string splashVfxBarcode = string.Empty;

        [Tooltip("The MonoDisc barcode of the splash SFX. If empty, uses the default water audio.")]
        public string splashSfxBarcode = string.Empty;

        [Tooltip("Is the splash enabled?")]
        public bool splashEnabled = true;

        public void SetAmbience(string barcode)
        {
        }

        public void ToggleAmbience(bool enabled)
        {
        }

        public void SetSplashVFX(string barcode)
        {
        }

        public void SetSplashSFX(string barcode)
        {
        }

        public void ToggleSplash(bool enabled)
        {
        }

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;

            Gizmos.color = Color.blue;

            Gizmos.DrawWireCube(center, size);
        }

        [MenuItem("GameObject/Fluids/Liquid Volume", priority = 1)]
        private static void MenuCreateVolume(MenuCommand menuCommand)
        {
            GameObject go = new("Liquid Volume", typeof(LiquidVolume));
            go.transform.localScale = Vector3.one;

            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Selection.activeObject = go;

            go.AddComponent<BoxCollider>().isTrigger = true;

            Undo.RegisterCreatedObjectUndo(go, "Create Liquid Volume");
        }
#endif
    }
}
