using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Use owned fixed Game-view resolutions, then remove only the presets this test created.</summary>
    public sealed class UiViewportScope : IDisposable
    {
        private const BindingFlags PublicInstance=BindingFlags.Instance|BindingFlags.Public;
        private readonly EditorWindow view;
        private readonly object group;
        private readonly Type sizeType;
        private readonly PropertyInfo selection;
        private readonly int previous;
        private readonly List<object> owned=new List<object>();

        /// <summary>Retain the current Editor viewport selection without saving shared size preferences.</summary>
        public UiViewportScope()
        {
            var assembly=typeof(EditorWindow).Assembly;
            Type viewType=assembly.GetType("UnityEditor.GameView");
            view=(EditorWindow)viewType.BaseType.GetMethod("GetMainPlayModeView",BindingFlags.Static|BindingFlags.NonPublic)
                .Invoke(null,null) ?? EditorWindow.GetWindow(viewType);
            selection=viewType.GetProperty("selectedSizeIndex",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            previous=(int)selection.GetValue(view);
            Type sizesType=assembly.GetType("UnityEditor.GameViewSizes");
            object sizes=sizesType.GetProperty("instance",BindingFlags.Static|BindingFlags.Public|BindingFlags.FlattenHierarchy).GetValue(null);
            group=sizesType.GetProperty("currentGroup",PublicInstance).GetValue(sizes);
            sizeType=assembly.GetType("UnityEditor.GameViewSize");
        }

        /// <summary>Select an actual render resolution; the caller waits for the next layout/repaint.</summary>
        public void Set(int width,int height)
        {
            ConstructorInfo constructor=Array.Find(sizeType.GetConstructors(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance),c=>c.GetParameters().Length==4);
            object kind=Enum.Parse(constructor.GetParameters()[0].ParameterType,"FixedResolution");
            object size=constructor.Invoke(new[]{kind,(object)width,height,"42 owned UI regression"});
            group.GetType().GetMethod("AddCustomSize").Invoke(group,new[]{size});owned.Add(size);
            int index=(int)group.GetType().GetMethod("GetTotalCount").Invoke(group,null)-1;
            view.GetType().GetMethod("SizeSelectionCallback").Invoke(view,new object[]{index,null});
            view.Focus();view.Repaint();
            UnityEngine.Debug.Log("UI viewport selected "+width+"x"+height+"; target="+
                view.GetType().GetProperty("targetRenderSize",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(view));
        }

        /// <summary>Restore the former selection and remove only owned in-memory presets.</summary>
        public void Dispose()
        {
            selection.SetValue(view,previous);
            Type type=group.GetType();int count=(int)type.GetMethod("GetTotalCount").Invoke(group,null);
            for(int index=count-1;index>=0;index--)
            {
                object size=type.GetMethod("GetGameViewSize").Invoke(group,new object[]{index});
                if(owned.Contains(size))type.GetMethod("RemoveCustomSize").Invoke(group,new object[]{index});
            }
            owned.Clear();view.Repaint();
        }
    }
}
