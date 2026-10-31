using System;
using System.Linq;
using System.Reflection;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;
using UnityEngine;
using UnityEngine.UIElements;

namespace FortyTwoAndBeyond.Editor
{
    /// <summary>Exercise live retained widgets and the rendered login layout across real viewport sizes.</summary>
    public sealed class UiLayoutRegression : IDisposable
    {
        private readonly SpaceClient client;
        private readonly FlightHud hud;
        private readonly VisualElement root;
        private readonly UiViewportScope viewport=new UiViewportScope();
        private readonly FieldInfo scaleField=typeof(FlightHud).GetField("interfaceSize",BindingFlags.Instance|BindingFlags.NonPublic);
        private readonly float previousScale;
        private readonly Vector2Int[] sizes={new Vector2Int(960,640),new Vector2Int(1366,768),new Vector2Int(1920,1080),new Vector2Int(2560,1440),new Vector2Int(3840,2160)};
        private int size,stage=-1;
        private double after;
        private Snapshot crewPreview;
        private bool organicPreview;
        private readonly object deck;
        private readonly MethodInfo refreshCrew;
        private Vector2 originalTarget,originalSystems;

        /// <summary>Keep the existing interface preference and use only the owned test's client.</summary>
        public UiLayoutRegression(SpaceClient client,FlightHud hud,VisualElement root)
        {this.client=client;this.hud=hud;this.root=root;previousScale=(float)scaleField.GetValue(hud);
            deck=typeof(FlightHud).GetField("deck",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(hud);
            refreshCrew=deck.GetType().GetMethod("RefreshCrew",BindingFlags.Instance|BindingFlags.NonPublic);
        }

        /// Returns:
        ///     bool: Whether all viewport, widget interaction and login checks have completed.
        public bool Step(double now)
        {
            if(crewPreview!=null)
            {
                refreshCrew.Invoke(deck,new object[]{crewPreview});
                for(int index=0;index<5;index++)root.Q<ResourceDialWidget>("crew-defense-"+index).Tick(Time.unscaledTime);
                if(organicPreview)
                {
                    var actor=new ShipActor(42,1,1,"Organic contact",default,new float[]{0,0,0,1},true,isPlayer:false,shield:.64f,hull:0,maxHull:0,maxShield:100000);
                    deck.GetType().GetMethod("ShowMobTooltip",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(deck,new object[]{actor,new Vector2(650,240)});
                }
            }
            if(now<after)return false;
            if(stage==-1)
            {
                VisualElement bar=root.Q("shortcut-bar"),handle=root.Q("shortcut-bar-header");
                originalSystems=bar.worldBound.position;Vector2 from=handle.worldBound.center;
                Pointer(handle,EventType.MouseDown,from);Pointer(handle,EventType.MouseDrag,from+new Vector2(-70,-90));Pointer(handle,EventType.MouseUp,from+new Vector2(-70,-90));
                stage=-2;after=now+.25;return false;
            }
            if(stage==-2)
            {
                Require(Vector2.Distance(root.Q("shortcut-bar").worldBound.position,originalSystems)>10,"Systems header drag did not move the bar");
                stage=0;return false;
            }
            if(stage==0)
            {
                Vector2Int dimensions=sizes[size];viewport.Set(dimensions.x,dimensions.y);
                scaleField.SetValue(hud,size<2?1f:1.3f);stage=1;after=now+.7;return false;
            }
            if(stage==1)
            {
                Require(Screen.width==sizes[size].x&&Screen.height==sizes[size].y,"Game view did not adopt "+sizes[size]+"; actual "+Screen.width+"x"+Screen.height);
                foreach(string key in new[]{"overview","destinations","target","integrity","propulsion"})
                {
                    Button toggle=root.Q<Button>("collapse-"+key);
                    if(root.Q("content-"+key).resolvedStyle.display==DisplayStyle.None)Click(toggle);
                }
                stage=2;after=now+.25;return false;
            }
            if(stage==2)
            {
                foreach(string key in new[]{"overview","destinations","target","integrity","propulsion"})
                    Inside(root.Q("panel-"+key),"flight panel "+key);
                Inside(root.Q("expand-systems"),"Systems expansion control");
                var actor=client.CurrentSnapshot.Mobs.First();
                Require(actor.MaxShield>0&&root.Q("panel-target").Q<Label>("gauge-shield-value").text.Contains(actor.MaxShield.ToString("N0")),
                    "Target shield display does not use the authoritative capacity");
                Click(root.Q("collapse-destinations"));stage=3;after=now+.25;return false;
            }
            if(stage==3)
            {
                Require(root.Q("content-destinations").resolvedStyle.display==DisplayStyle.None,"Collapse did not hide only the destination contents");
                Inside(root.Q("collapse-destinations"),"collapsed destination header");
                Click(root.Q("collapse-destinations"));Click(root.Q("open-systems"));stage=4;after=now+.4;return false;
            }
            if(stage==4)
            {
                Require(hud.EquipmentOpen,"Systems expansion did not open full fitting");
                Inside(root.Q("fitting-window"),"full fitting window");
                Inside(root.Q("expand-systems"),"embedded Systems control");
                CheckEmbeddedSystems();
                Require(root.Q<ScrollView>("systems-fitting-scroll")==null,"Expanded inventory still has an outer scroller");
                Require(root.Q<ScrollView>("fitting-cargo-scroll")!=null,"Cargo lost its own scroller");
                Inside(root.Q("fitting-mount-ring"),"entire mount assembly without scrolling");
                foreach(string key in new[]{"destinations","target"})
                    Require(root.Q("panel-"+key).resolvedStyle.display!=DisplayStyle.None,"Opening inventory removed the "+key+" widget");
                Button close=root.Q("fitting-window").Query<Button>().ToList().First(button=>button.text.StartsWith("Close",StringComparison.Ordinal));
                Inside(close,"fitting close button");
                ScreenCapture.CaptureScreenshot("Builds/verification/ui-fitting-"+sizes[size].x+"x"+sizes[size].y+".png");
                stage=41;after=now+.2;return false;
            }
            if(stage==41)
            {
                if(size==0)
                {
                    for(int index=6;index<24;index++)Click(root.Q("add-shortcut"));
                    stage=42;after=now+.4;return false;
                }
                Click(root.Q("expand-systems"));stage=5;after=now+.3;return false;
            }
            if(stage==42)
            {
                CheckEmbeddedSystems();Inside(root.Q("shortcut-23"),"last of 24 embedded Systems shortcuts");
                ScreenCapture.CaptureScreenshot("Builds/verification/ui-fitting-expanded-24.png");
                stage=43;after=now+.2;return false;
            }
            if(stage==43)
            {
                for(int index=6;index<24;index++)Click(root.Q("remove-shortcut"));
                stage=44;after=now+.3;return false;
            }
            if(stage==44)
            {Click(root.Q("expand-systems"));stage=5;after=now+.3;return false;}
            if(stage==5)
            {
                Require(!hud.EquipmentOpen,"Systems collapse did not return to flight");
                Inside(root.Q("panel-destinations"),"destinations after inventory close");Inside(root.Q("panel-target"),"target after inventory close");
                VisualElement panel=root.Q("panel-target");originalTarget=panel.worldBound.position;
                VisualElement handle=panel.Q<Label>();Vector2 from=handle.worldBound.center;
                Pointer(handle,EventType.MouseDown,from);Pointer(handle,EventType.MouseDrag,from+new Vector2(-100,-45));Pointer(handle,EventType.MouseUp,from+new Vector2(-100,-45));
                stage=6;after=now+.25;return false;
            }
            if(stage==6)
            {
                VisualElement panel=root.Q("panel-target");Inside(panel,"dragged selected-target widget");
                Require(Vector2.Distance(panel.worldBound.position,originalTarget)>10,"Target header drag did not move its widget");
                stage=61;after=now+.2;return false;
            }
            if(stage==61)
            {
                VisualElement talk=root.Q("open-chat");Inside(talk,"Talk button");
                VisualElement hit=root.panel.Pick(talk.worldBound.center);
                Require(hit==talk||talk.Contains(hit),"Talk button is covered by "+hit?.name);
                Click(talk);stage=7;after=now+.3;return false;
            }
            if(stage==7)
            {
                Require(client.ChatOpen,"Talk did not open chat entry");
                Inside(root.Q("chat-entry"),"chat input");Inside(root.Q("send-chat"),"chat Send control");
                Require(root.Q("chat-entry").resolvedStyle.fontSize>=18,"Chat input font became unreadable");
                var input=root.Q<TextField>("chat-entry");
                Require(input.Q(className:TextField.inputUssClassName).customStyle.TryGetValue(new CustomStyleProperty<Color>("--unity-cursor-color"),out Color caret)
                    &&caret.r>.85f&&caret.g>.9f,"Chat caret lacks contrast");
                Require(input.focusController.focusedElement is VisualElement focused&&(focused==input||input.Contains(focused)),"Chat entry did not receive typing focus");
                input.value="/d_spawn Tengu 50";input.SelectRange(8,8);
                ScreenCapture.CaptureScreenshot("Builds/verification/ui-chat-"+sizes[size].x+"x"+sizes[size].y+".png");
                stage=71;after=now+.25;return false;
            }
            if(stage==71)
            {
                root.Q<TextField>("chat-entry").value="";client.CloseChatEntry();
                if(++size<sizes.Length){stage=0;return false;}
                viewport.Set(1920,1080);scaleField.SetValue(hud,1.15f);
                Click(root.Q("open-systems"));stage=80;after=now+.4;return false;
            }
            if(stage==80)
            {Click(root.Q("reset-flight-layout"));stage=81;after=now+.4;return false;}
            if(stage==81)
            {Click(root.Q("close-fitting"));stage=82;after=now+.4;return false;}
            if(stage==82)
            {Click(root.Q("open-chat"));stage=83;after=now+.4;return false;}
            if(stage==83)
            {
                Require(client.ChatOpen,"Default chat layout did not open");
                var input=root.Q<TextField>("chat-entry");input.value="/d_spawn Tengu 50";input.SelectRange(8,8);
                foreach(string key in new[]{"defense","reactor"})
                {
                    VisualElement instrument=root.Q("panel-integrity").Q("gauge-"+key);Inside(instrument,"default "+key+" instrument");
                    Require(instrument is ResourceDialWidget&&instrument.Q<Image>().image is RenderTexture texture&&texture.IsCreated(),
                        "Live "+key+" dial did not allocate its GPU face");
                    Require(instrument.Q("gauge-caption").resolvedStyle.display==DisplayStyle.None,"Instrument name remained printed on the face");
                    Require(instrument.Q<Label>("gauge-percentage").text.Contains("%"),"Centered resource percentage is missing");
                }
                ScreenCapture.CaptureScreenshot("Builds/verification/ui-polished-flight.png");stage=84;after=now+.3;return false;
            }
            if(stage==84)
            {root.Q<TextField>("chat-entry").value="";client.CloseChatEntry();stage=85;after=now+.3;return false;}
            if(stage==85)
            {ScreenCapture.CaptureScreenshot("Builds/verification/ui-polished-flight-clear.png");stage=86;after=now+.3;return false;}
            if(stage==86)
            {
                client.enabled=false; // Freeze only this owned fixture while rendering deterministic crew snapshots.
                var world=new World(1076);world.Apply(new Frame(0xffff,Array.Empty<byte>()),0x40000001);
                var members=new GroupMember[5];
                for(int index=0;index<5;index++)
                {
                    uint id=0x40000002u+(uint)index;members[index]=new GroupMember(id,"Pilot "+(index+1));
                    if(index<4)world.Players[id]=new ShipActor(id,1,1,members[index].Name,new FortyTwoAndBeyond.Protocol.Position(1000+index*100,0,0),new float[]{0,0,0,1},true,
                        shield:.9f-index*.15f,hull:72000-index*10000,maxHull:72000,maxShield:100000);
                }
                typeof(World).GetProperty("Group").SetValue(world,new GroupState(true,members));
                crewPreview=new Snapshot(world,"owned crew rendering fixture",true);
                stage=87;after=now+.4;return false;
            }
            if(stage==87)
            {
                Inside(root.Q("panel-crew"),"five-pilot crew instruments");
                for(int index=0;index<5;index++)Inside(root.Q("crew-member-"+index),"crew member "+index);
                Require(root.Q("crew-defense-4").Q<Label>("gauge-shield-value").text.Contains("NO DATA"),"Missing group telemetry was presented as full health");
                ScreenCapture.CaptureScreenshot("Builds/verification/ui-combined-five-crew.png");stage=871;after=now+.3;return false;
            }
            if(stage==871)
            {
                var show=deck.GetType().GetMethod("ShowGaugeHover",BindingFlags.Instance|BindingFlags.NonPublic);
                foreach(string selector in new[]{"gauge-defense","crew-defense-1","crew-defense-0"})
                {
                    var instrument=root.Q<ResourceDialWidget>(selector);
                    show.Invoke(deck,new object[]{instrument,instrument.worldBound.center});
                    string text=root.Q<Label>("resource-hover-values").text;
                    Require(text.Contains("Shield:")&&text.Contains("Hull:")&&text.Contains(" / "),"Hover omitted real current/max values for "+selector);
                }
                stage=873;after=now+.25;return false;
            }
            if(stage==873)
            {
                Inside(root.Q("resource-hover"),"real-value resource hover");
                ScreenCapture.CaptureScreenshot("Builds/verification/ui-combined-hover.png");stage=872;after=now+.3;return false;
            }
            if(stage==872)
            {
                root.Q("resource-hover").style.display=DisplayStyle.None;
                deck.GetType().GetField("hoveredGauge",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(deck,null);
                stage=88;return false;
            }
            if(stage==88)
            {organicPreview=true;stage=89;after=now+.4;return false;}
            if(stage==89)
            {
                var target=root.Q<ResourceDialWidget>("mob-tooltip-defense");
                Inside(root.Q("mob-resource-tooltip"),"mob hover resource tooltip");
                Require(root.Q<Label>("mob-tooltip-values").text.Contains("Shield:"),"Mob tooltip omitted shield values");
                Require(!target.HasHull&&target.Q("gauge-hull-value").resolvedStyle.display==DisplayStyle.None,"Organic contact retained a hull readout");
                ScreenCapture.CaptureScreenshot("Builds/verification/ui-combined-organic.png");stage=90;after=now+.3;return false;
            }
            if(stage==90)
            {crewPreview=null;organicPreview=false;client.enabled=true;client.Disconnect();size=0;stage=100;return false;}
            if(stage==100)
            {
                Vector2Int dimensions=size==0?sizes[0]:sizes[sizes.Length-1];viewport.Set(dimensions.x,dimensions.y);
                scaleField.SetValue(hud,size==0?1f:1.3f);stage=101;after=now+.7;return false;
            }
            if(stage==101)
            {
                Rect button=hud.LoginSubmitBounds,screen=new Rect(0,0,Screen.width,Screen.height);
                Require(button.width>100&&button.height>20&&screen.Contains(button.min)&&screen.Contains(button.max),"Rendered ENTER SPACE button is hidden or clipped: "+button);
                ScreenCapture.CaptureScreenshot("Builds/verification/ui-login-"+Screen.width+"x"+Screen.height+".png");
                if(++size<2){stage=100;return false;}
                stage=102;after=now+.3;return false;
            }
            return stage==102;
        }

        /// <summary>A dragged bar must dock entirely inside fitting, without retaining flight-relative offsets.</summary>
        private void CheckEmbeddedSystems()
        {
            VisualElement bar=root.Q("shortcut-bar"),fitting=root.Q("fitting-window");Inside(bar,"embedded Systems shortcuts");
            Require(fitting.Contains(bar)&&fitting.worldBound.Contains(bar.worldBound.min)&&fitting.worldBound.Contains(bar.worldBound.max),
                "Systems shortcuts escaped the inventory after dragging or resizing");
        }

        /// <summary>Require a visible, fully reachable control or panel inside the actual UI root.</summary>
        private void Inside(VisualElement element,string label)
        {
            Require(element!=null,"Missing "+label);
            Rect bounds=element.worldBound,view=root.worldBound;view.xMin-=2;view.yMin-=2;view.xMax+=2;view.yMax+=2;
            Require(element.resolvedStyle.display!=DisplayStyle.None&&element.resolvedStyle.visibility==Visibility.Visible&&bounds.width>1&&bounds.height>1&&view.Contains(bounds.min)&&view.Contains(bounds.max),
                label+" is hidden/clipped at "+Screen.width+"x"+Screen.height+": "+bounds+" outside "+view);
        }
        /// <summary>Report the concrete UI regression rather than continuing with a partial pass.</summary>
        private static void Require(bool valid,string message){if(!valid)throw new InvalidOperationException(message);}
        /// <summary>Dispatch the normal retained button pointer path.</summary>
        private static void Click(VisualElement element){Pointer(element,EventType.MouseDown,element.worldBound.center);Pointer(element,EventType.MouseUp,element.worldBound.center);}
        /// <summary>Send a pointer phase to the widget's registered handlers.</summary>
        private static void Pointer(VisualElement element,EventType type,Vector2 point)
        {
            var input=new Event{type=type,button=0,mousePosition=point};
            if(type==EventType.MouseDown){using(var evt=PointerDownEvent.GetPooled(input))element.SendEvent(evt);}
            else if(type==EventType.MouseDrag){using(var evt=PointerMoveEvent.GetPooled(input))element.SendEvent(evt);}
            else{using(var evt=PointerUpEvent.GetPooled(input))element.SendEvent(evt);}
        }
        /// <summary>Restore the original interface preference and remove owned Editor presets.</summary>
        public void Dispose(){scaleField.SetValue(hud,previousScale);viewport.Dispose();}
    }
}
