#if UNITY_EDITOR

using System;
using UltEvents;
using UnityEditor;

namespace UltExtensions
{
    public static class TestUltExtension
    {
        [MenuItem("CONTEXT/UltEventHolder/TestExtension")]
        private static void TestExtension(MenuCommand command)
        {
            var ultEventHolder = (UltEventHolder)command.context;

            Action<bool> setActiveAction = ultEventHolder.gameObject.SetActive;
            var setActiveCall = new PersistentCall(setActiveAction);
            
            var calls = new[]
            {
                setActiveCall,
                setActiveCall,
                setActiveCall
            };
            
            ultEventHolder.Event.AddNonDestructiveCalls(1, calls);
        }
    }
}

#endif