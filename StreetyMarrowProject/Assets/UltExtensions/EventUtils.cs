using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UltEvents;

public static class EventUtils
{
    public class ReturnValueTarget
    {
        private readonly UltEvent _ultEvent;
        private readonly PersistentCall _target;
        
        public ReturnValueTarget(UltEvent ultEvent, int targetIndex)
        {
            _target = ultEvent.PersistentCallsList[targetIndex];
            ultEvent = ultEvent;
        }

        public static implicit operator int(ReturnValueTarget returnValueTarget)
        {
            var index = 0;
            foreach (var call in returnValueTarget._ultEvent.PersistentCallsList)
            {
                if (call == returnValueTarget._target)
                    return index;
                
                Debug.Log($"Restored {index}");
                
                index++;
            }

            throw new Exception("Failed to locate call");
        }
    }

    public class TargetSaver
    {
        private readonly Dictionary<PersistentArgument, ReturnValueTarget> _returnValueTargetLookup;
        private UltEvent ultEvent;

        public TargetSaver(ref UltEvent ultEvent)
        {
            this.ultEvent = ultEvent;
        }
        
        public void Save()
        {
            foreach (var call in ultEvent.PersistentCallsList) Save(call);
        }

        public void Save(PersistentCall[] calls)
        {
            foreach (var call in calls) Save(call);
        }
        
        public void Save(PersistentCall call)
        {
            foreach (var argument in call.PersistentArguments) Save(argument);
        }
        
        private void Save(PersistentArgument argument)
        {
            if (argument.Type == PersistentArgumentType.ReturnValue) _returnValueTargetLookup[argument] = new ReturnValueTarget(ultEvent, argument.ReturnedValueIndex);
        }

        public void FixReturnValues()
        {
            foreach (var call in ultEvent.PersistentCallsList)
            {
                Debug.Log($"Try restore {call.Target}");
                
                foreach (var argument in call.PersistentArguments)
                {
                    Debug.Log(argument.Type);
                    
                    if (argument.Type == PersistentArgumentType.ReturnValue)
                    {
                        Debug.Log($"Restoring {argument.Object}");
                        argument.ReturnedValueIndex = _returnValueTargetLookup[argument];
                    }
                }
            }
        }
    }
    
    public static void AddNonDestructiveCalls(this UltEvent ultEvent, int index, PersistentCall[] calls)
    {
        var targetSaver = new TargetSaver(ref ultEvent);
        targetSaver.Save();
        targetSaver.Save(calls);
        
        foreach (var call in calls.Reverse())
        {
            targetSaver.Save(call);
            ultEvent.PersistentCallsList.Insert(index, call);
        }
        
        targetSaver.FixReturnValues();
    }
}
