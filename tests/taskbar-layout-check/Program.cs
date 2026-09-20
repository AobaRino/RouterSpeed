using RouterSpeed;
using System.Diagnostics;
using System.Reflection;
var screen = new Rectangle(0,0,2560,1440);
var bar = new Rectangle(0,1392,2560,48);
var tray = new Rectangle(2278,1392,282,48);
Rectangle[] buttons = [new(6,1392,152,48),new(1014,1392,45,48),new(1503,1392,44,48)];
var cases = new (string,Action)[]
{
 ("Known Win11 gap excludes application buttons and tray margins",()=>{Check(TaskbarLayout.TryCalculate(bar,screen,tray,buttons,96,out var r,out _));Check(r==new Rectangle(1553,1394,719,44));}),
 ("An expanded application row reduces available width",()=>{Check(TaskbarLayout.TryCalculate(bar,screen,tray,[new(1013,1392,1187,48)],96,out var r,out _));Check(r.Width==66);}),
 ("Overlapping application buttons leave no safe gap",()=>Check(!TaskbarLayout.TryCalculate(bar,screen,tray,[new(2200,1392,100,48)],96,out _,out _))),
 ("Tray expansion moves the gap boundary left",()=>{Check(TaskbarLayout.TryCalculate(bar,screen,new(2100,1392,460,48),buttons,96,out var r,out _));Check(r.Right==2094);}),
 ("Controls outside the taskbar do not consume the gap",()=>{Check(TaskbarLayout.TryCalculate(bar,screen,tray,[..buttons,new(0,0,10000,800)],96,out var r,out _));Check(r.Right==2272&&r.Left==1553);}),
 ("A flyout crossing the taskbar edge is not a row button",()=>{Check(TaskbarLayout.TryCalculate(bar,screen,tray,[..buttons,new(1700,1360,500,48)],96,out var r,out _));Check(r.Left==1553&&r.Right==2272);}),
 ("A tall overflow popup cannot shrink the taskbar gap",()=>{Check(TaskbarLayout.TryCalculate(bar,screen,tray,[..buttons,new(1700,1350,500,100)],96,out var r,out _));Check(r.Left==1553&&r.Right==2272);}),
 ("Confirmed occupied layout is distinct from unknown geometry",()=>{Check(!TaskbarLayout.TryCalculate(bar,screen,tray,[new(2200,1392,100,48)],96,out _,out _,out bool confirmed)&&confirmed);Check(!TaskbarLayout.TryCalculate(bar,screen,tray,[],96,out _,out _,out confirmed)&&!confirmed);}),
 ("Unknown occupied region never guesses a free interval",()=>Check(!TaskbarLayout.TryCalculate(bar,screen,tray,[],96,out _,out _))),
 ("Vertical taskbar falls back safely",()=>Check(!TaskbarLayout.TryCalculate(new(0,0,48,1440),screen,new(0,1300,48,140),buttons,96,out _,out _))),
 ("Auto-hidden taskbar with only two pixels visible is unavailable",()=>{Check(!TaskbarLayout.IsFullyVisible(new(0,1438,2560,48),screen));Check(!TaskbarLayout.TryCalculate(new(0,1438,2560,48),screen,tray,buttons,96,out _,out _));}),
 ("Per-monitor DPI scales only the safety margins",()=>{Check(TaskbarLayout.TryCalculate(new(0,1368,2560,72),screen,new(2200,1368,360,72),[new(1000,1368,500,72)],144,out var r,out _));Check(r==new Rectangle(1509,1371,682,66));}),
 ("Negative monitor coordinates preserve correct global bounds",()=>{Check(TaskbarLayout.TryCalculate(new(-1920,-48,1920,48),new(-1920,-1080,1920,1080),new(-200,-48,200,48),[new(-1000,-48,400,48)],96,out var r,out _));Check(r==new Rectangle(-594,-46,388,44));}),
 ("Tray outside its taskbar is rejected",()=>Check(!TaskbarLayout.TryCalculate(bar,screen,new(2700,1392,200,48),buttons,96,out _,out _))),
 ("Normal maximized work-area windows are not fullscreen",()=>Check(!TaskbarLayout.IsFullscreenCover(new(0,0,2560,1392),screen,true,true))),
 ("Maximized captioned windows with autohide are not fullscreen",()=>Check(!TaskbarLayout.IsFullscreenCover(screen,screen,true,true))),
 ("Borderless fullscreen windows are detected",()=>Check(TaskbarLayout.IsFullscreenCover(screen,screen,false,false))),
 ("Fullscreen on a different monitor does not hide the primary bar",()=>Check(!TaskbarLayout.IsFullscreenCover(new(2560,0,2560,1440),screen,false,false))),
 ("Stale UIA geometry expires while fast fullscreen detection still works",Staleness),
 ("Transient geometry can be retained only with fresh unchanged anchors",TransientRetention),
 ("Unexpected accessibility exceptions cannot kill the worker",ProviderException),
 ("A hung provider cannot block disposal",BoundedDispose)
};
int failures=0;
foreach(var (name,run) in cases){try{run();Console.WriteLine("PASS "+name);}catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex.Message);}}
Console.WriteLine($"{cases.Length-failures}/{cases.Length} passed");
return failures==0?0:1;
static void Check(bool condition){if(!condition)throw new InvalidOperationException("Assertion failed");}
static TaskbarDockSnapshot Valid()=>new(new(0,1000,1920,48),new(1500,1002,200,44),96,false,"test",true,DateTimeOffset.UtcNow,LayoutConfirmed:true);
static void Staleness()
{
 using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();int count=0;int hide=0;
 using var monitor=new TaskbarDockMonitor(()=>{if(Interlocked.Increment(ref count)==2){entered.Set();release.Wait();}return Valid();},()=>Volatile.Read(ref hide)!=0,_=>true);
 monitor.Start();Check(SpinWait.SpinUntil(()=>monitor.Current.IsAvailable,2000));Check(entered.Wait(2000));
 try
 {
  typeof(TaskbarDockMonitor).GetField("_publishedAt",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(monitor,Stopwatch.GetTimestamp()-4*Stopwatch.Frequency);
  typeof(TaskbarDockMonitor).GetField("_lastAvailableAt",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(monitor,Stopwatch.GetTimestamp()-4*Stopwatch.Frequency);
  Check(!monitor.Current.IsAvailable&&monitor.Current.AvailableArea.IsEmpty&&!monitor.Current.LayoutConfirmed&&!monitor.Current.CanKeepPlacement);
  Volatile.Write(ref hide,1);Check(monitor.Current.ShouldHide);
  var field=typeof(TaskbarDockMonitor).GetField("_worker",BindingFlags.Instance|BindingFlags.NonPublic)!;object? worker=field.GetValue(monitor);
  monitor.Start();monitor.Start();monitor.Stop();Check(!monitor.Current.IsAvailable);monitor.Start();Check(ReferenceEquals(worker,field.GetValue(monitor)));
 }
 finally{release.Set();}
 Check(SpinWait.SpinUntil(()=>monitor.Current.IsAvailable,2000));
}
static void TransientRetention()
{
 using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();int count=0;int anchorsMatch=1;int hide=0;
 using var monitor=new TaskbarDockMonitor(()=>{if(Interlocked.Increment(ref count)==2){entered.Set();release.Wait();}return Valid();},()=>Volatile.Read(ref hide)!=0,_=>Volatile.Read(ref anchorsMatch)!=0);
 monitor.Start();Check(SpinWait.SpinUntil(()=>monitor.Current.IsAvailable,2000));Check(entered.Wait(2000));
 try
 {
  var type=typeof(TaskbarDockMonitor);var flags=BindingFlags.Instance|BindingFlags.NonPublic;
  var original=monitor.Current;Check(original.LayoutConfirmed);
  Volatile.Write(ref anchorsMatch,0);
  Check(!monitor.Current.IsAvailable&&!monitor.Current.CanKeepPlacement&&monitor.Current.AvailableArea.IsEmpty&&!monitor.Current.LayoutConfirmed);
  Volatile.Write(ref anchorsMatch,1);
  type.GetField("_snapshot",flags)!.SetValue(monitor,new TaskbarDockSnapshot(Rectangle.Empty,Rectangle.Empty,96,false,"transient",false,DateTimeOffset.UtcNow));
  type.GetField("_lastAvailableAt",flags)!.SetValue(monitor,Stopwatch.GetTimestamp()-Stopwatch.Frequency);
  var kept=monitor.Current;Check(!kept.IsAvailable&&!kept.LayoutConfirmed&&kept.CanKeepPlacement&&kept.AvailableArea==original.AvailableArea&&kept.TaskbarBounds==original.TaskbarBounds);
  Volatile.Write(ref hide,1);Check(monitor.Current.ShouldHide);
  Volatile.Write(ref anchorsMatch,0);Check(!monitor.Current.CanKeepPlacement&&monitor.Current.AvailableArea.IsEmpty);
  Volatile.Write(ref anchorsMatch,1);type.GetField("_lastAvailableAt",flags)!.SetValue(monitor,Stopwatch.GetTimestamp()-3*Stopwatch.Frequency);
  Check(!monitor.Current.CanKeepPlacement&&monitor.Current.AvailableArea.IsEmpty);
 }
 finally{release.Set();}
}
static void ProviderException()
{
 using var monitor=new TaskbarDockMonitor(()=>throw new TimeoutException("private provider detail"),()=>false);
 monitor.Start();Check(SpinWait.SpinUntil(()=>monitor.Current.Detail.StartsWith("暂时无法"),2000));Check(!monitor.Current.IsAvailable);Check(!monitor.Current.Detail.Contains("private"));
}
static void BoundedDispose()
{
 using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
 var monitor=new TaskbarDockMonitor(()=>{entered.Set();release.Wait();return Valid();},()=>false);monitor.Start();Check(entered.Wait(2000));
 try{var timer=Stopwatch.StartNew();monitor.Dispose();Check(timer.ElapsedMilliseconds<500);}finally{release.Set();}
}
