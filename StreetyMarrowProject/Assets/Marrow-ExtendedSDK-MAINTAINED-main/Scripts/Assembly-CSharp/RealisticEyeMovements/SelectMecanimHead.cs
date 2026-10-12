#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace RealisticEyeMovements {
	
	public static class SelectMecanimHead
	{
	
		[MenuItem("Tools/Realistic Eye Movements/Select Mecanim Head %#h")]
		public static void SelectHead()
		{
			if ( Selection.transforms == null || Selection.transforms.Length == 0 )
			{
				ShowError("Please select the character you want to find the Mecanim head bone of.");
				return;
			}
			
			Transform t = Selection.transforms[0];
			
			if ( false == t.gameObject.activeInHierarchy )
			{
				ShowError("GameObject needs to be active to find the head bone.");
				return;
			}
			
			if ( t == null )
			{
				ShowError("Please select the character you want to find the Mecanim head bone of.");
				return;
			}
			
			Animator animator = t.GetComponentInChildren<Animator>();
			if ( animator == null )
			{
				animator = t.GetComponentInParent<Animator>();
				if ( animator == null )
				{
					Transform transformWithHeadPrefix = FindRecursivelyWithPrefix(t, "head");
					if ( transformWithHeadPrefix == null )
					{
					ShowError("No Animator component found.");
					return;
				}
					
					Selection.objects = new Object[] { transformWithHeadPrefix.gameObject };
					return;
				}
			}
			
			Transform head = Utils.GetHumanoidBone(animator, HumanBodyBones.Head);
			if ( head == null )
			{
				ShowError("No Mecanim head bone found. Is the charter rig set to Humanoid and a head bone assigned?");
				return;
			}
			
			Selection.objects = new Object[] { head.gameObject };
		}
		
		
		public static Transform FindRecursivelyWithPrefix(Transform startXform, string prefix)
		{
			if ( startXform.name.ToLower().StartsWith(prefix) )
				return startXform;
			
			foreach ( Transform childXform in startXform )
			{
				Transform foundXform = FindRecursivelyWithPrefix(childXform, prefix);
				if ( foundXform != null )
					return foundXform;
			}
			
			return null;
		}
		
		
		static void ShowError(string errorMessage)
		{
			EditorUtility.DisplayDialog("Cannot select Mecanim head",
								"ERROR\n\n" + errorMessage,
								"Ok");
		}
	
	
	}
}
#endif