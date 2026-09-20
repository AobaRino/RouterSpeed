using System.Text.Json;
using RouterSpeed;

internal static partial class Program
{
    private static void RunTimingChecks()
    {
        Run("Stable polling issues no resize, move or native placement request", StablePolling);
        Run("Twenty authorized transient failures preserve the compact bar", TransientFlaps);
        Run("Unknown anchor hides immediately without switching to floating", UntrustedTransient);
        Run("Twenty unknown/single-valid cycles stay hidden without flicker", UntrustedRecoveryFlaps);
        Run("CanKeepPlacement cannot revive an untrusted hidden anchor", KeepCannotBypassRecovery);
        Run("Confirmed insufficient space waits two seconds and distinct samples", ConfirmedFailureTiming);
        Run("Stable recovery needs independent samples and 650 milliseconds", RecoveryTiming);
        Run("A single cached sample cannot confirm fallback or redocking", CachedSampleTiming);
        Run("Valid samples reset accumulated failure periods", InterruptedFailureTiming);
        Run("Unknown layout gets a delayed fallback after ten seconds", UnknownFailureTiming);
        Run("Manual hide is honored during all timing and recovery paths", HiddenDuringTiming);
        Run("Fullscreen hides immediately and clears fallback timing", FullscreenDuringTiming);
        Run("Changing targets need stability and do not move on short jitter", TargetJitter);
    }

    private static TaskbarDockSnapshot Transient(Harness h,bool keep) => h.Snapshot with
    {
        IsAvailable=false,LayoutConfirmed=false,CanKeepPlacement=keep,
        AvailableArea=keep?h.Snapshot.AvailableArea:Rectangle.Empty,
        ShouldHide=false,Detail="测试：托盘布局过渡"
    };

    private static TaskbarDockSnapshot Insufficient(Harness h) => h.Snapshot with
    {
        IsAvailable=false,LayoutConfirmed=true,CanKeepPlacement=false,
        AvailableArea=Rectangle.Empty,ShouldHide=false,Detail="测试：已确认托盘空间不足"
    };

    private static void StablePolling()
    {
        using var h=new Harness(taskbar:true);
        using var native=new PlacementWatch(h.Bar);
        int resize=0,move=0,visibility=0;
        h.Bar.Resize+=(_,_)=>resize++;h.Bar.LocationChanged+=(_,_)=>move++;h.Bar.VisibleChanged+=(_,_)=>visibility++;
        Rectangle expected=h.Bar.Bounds;
        for(int i=0;i<20;i++) h.Step(350,i%2==0);
        Check(resize==0&&move==0&&visibility==0&&h.Bar.Bounds==expected,"Stable sampling caused layout/visibility churn.");
        Check(native.PositionRequests==0,$"Stable sampling caused {native.PositionRequests} native placement requests.");
    }

    private static void TransientFlaps()
    {
        using var h=new Harness(taskbar:true);
        using var native=new PlacementWatch(h.Bar);
        int resize=0,move=0,visibility=0;
        h.Bar.Resize+=(_,_)=>resize++;h.Bar.LocationChanged+=(_,_)=>move++;h.Bar.VisibleChanged+=(_,_)=>visibility++;
        TaskbarDockSnapshot stable=h.Snapshot;Rectangle original=h.Bar.Bounds;
        for(int i=0;i<20;i++)
        {
            h.Snapshot=Transient(h,true);h.Step(350);
            Check(Field<bool>(h.Bar,"_isDocked")&&h.Bar.Visible&&h.Bar.Bounds==original,"Authorized short failure changed presentation.");
            h.Snapshot=stable;h.Step(350);
        }
        Check(resize==0&&move==0&&visibility==0,"Transient sampling caused resize/move/show-hide flicker.");
        Check(native.PositionRequests==0,"Transient sampling caused native Z-order churn.");
        var report=new { cycles=20,resizes=resize,moves=move,visibilityChanges=visibility,nativePositionRequests=native.PositionRequests,finalDocked=Field<bool>(h.Bar,"_isDocked") };
        File.WriteAllText(Path.Combine(Output,"transient-after.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
    }

    private static void UntrustedTransient()
    {
        using var h=new Harness(taskbar:true);
        TaskbarDockSnapshot stable=h.Snapshot;Rectangle original=h.Bar.Bounds;
        h.Snapshot=Transient(h,false);h.Step(350);
        Check(!h.Bar.Visible&&Field<bool>(h.Bar,"_isDocked")&&h.Bar.Bounds==original,"Untrusted placement must hide without resize/fallback.");
        h.Snapshot=stable;h.Step(350);
        Check(!h.Bar.Visible&&h.Bar.Bounds==original,"One valid sample restored an untrusted same-position anchor too soon.");
        h.Step(650,false);
        Check(!h.Bar.Visible,"Holding the same valid sample for 650ms bypassed independent-sample confirmation.");
        h.Step(1,true);
        Check(h.Bar.Visible&&h.Bar.Bounds==original,"Two independent stable samples did not restore the same compact anchor.");
    }

    private static void UntrustedRecoveryFlaps()
    {
        using var h=new Harness(taskbar:true);
        TaskbarDockSnapshot stable=h.Snapshot;Rectangle original=h.Bar.Bounds;
        int resize=0,move=0,visibility=0;
        h.Bar.Resize+=(_,_)=>resize++;h.Bar.LocationChanged+=(_,_)=>move++;h.Bar.VisibleChanged+=(_,_)=>visibility++;
        for(int i=0;i<20;i++)
        {
            h.Snapshot=Transient(h,false);h.Step(350);
            Check(!h.Bar.Visible&&Field<bool>(h.Bar,"_isDocked"),"Unknown anchor did not remain safely hidden.");
            h.Snapshot=stable;h.Step(350);
            Check(!h.Bar.Visible&&Field<bool>(h.Bar,"_isDocked")&&h.Bar.Bounds==original,"Single recovered sample flickered or switched to floating.");
        }
        Check(resize==0&&move==0&&visibility==1,$"Untrusted recovery flapped: resize={resize}, move={move}, visibility={visibility}.");
        File.WriteAllText(Path.Combine(Output,"untrusted-recovery-after.json"),JsonSerializer.Serialize(new {
            cycles=20,resizes=resize,moves=move,visibilityChanges=visibility,finalVisible=h.Bar.Visible,finalDocked=Field<bool>(h.Bar,"_isDocked")
        },new JsonSerializerOptions{WriteIndented=true}));
        h.Step(650);
        Check(h.Bar.Visible&&visibility==2&&h.Bar.Bounds==original,"Stable recovery did not produce one final show.");
    }

    private static void KeepCannotBypassRecovery()
    {
        using var h=new Harness(taskbar:true);
        TaskbarDockSnapshot stable=h.Snapshot;Rectangle original=h.Bar.Bounds;
        h.Snapshot=Transient(h,false);h.Step(0);
        h.Snapshot=stable with {IsAvailable=false,LayoutConfirmed=false,CanKeepPlacement=true};
        h.Step(350);h.Step(650);
        Check(!h.Bar.Visible&&Field<bool>(h.Bar,"_isDocked")&&h.Bar.Bounds==original,"CanKeepPlacement bypassed an already required recovery wait.");
        h.Snapshot=stable;h.Step(0);h.Step(649);
        Check(!h.Bar.Visible,"Previously safe cached bounds shortened reliable recovery confirmation.");
        h.Step(1);
        Check(h.Bar.Visible&&h.Bar.Bounds==original,"Reliable independent recovery samples did not restore the bar.");
    }

    private static void ConfirmedFailureTiming()
    {
        using var h=new Harness(taskbar:true);Rectangle original=h.Bar.Bounds;
        h.Snapshot=Insufficient(h);h.Step(0);
        h.Step(1999);
        Check(Field<bool>(h.Bar,"_isDocked")&&h.Bar.Bounds==original,"Confirmed failure switched presentation before two seconds.");
        h.Step(1);
        Check(!Field<bool>(h.Bar,"_isDocked")&&h.Bar.Visible,"Continuous confirmed failure did not activate floating fallback at two seconds.");
        Check(Field<bool>(h.Bar,"_taskbarMode"),"Fallback changed user-selected display mode.");
    }

    private static void RecoveryTiming()
    {
        using var h=new Harness(taskbar:true);
        h.Snapshot=Insufficient(h);h.Step(0);h.Step(2000);
        Check(!Field<bool>(h.Bar,"_isDocked"),"Test did not enter fallback.");
        h.Snapshot=AvailableSnapshot(h.Bar.DeviceDpi);h.Step(0);
        h.Step(649);
        Check(!Field<bool>(h.Bar,"_isDocked"),"Recovery docked before 650ms of stable target.");
        h.Step(1);
        Check(Field<bool>(h.Bar,"_isDocked")&&h.Bar.Visible,"Stable recovery did not redock.");
    }

    private static void CachedSampleTiming()
    {
        using var h=new Harness(taskbar:true);
        h.Snapshot=Insufficient(h);h.Step(0);
        for(int i=0;i<8;i++) h.Step(350,false);
        Check(Field<bool>(h.Bar,"_isDocked"),"Re-reading one failed capture was counted as independent confirmation.");
        h.Step(1,true);
        Check(!Field<bool>(h.Bar,"_isDocked"),"Second independent failure did not confirm elapsed fallback.");
        h.Snapshot=AvailableSnapshot(h.Bar.DeviceDpi);h.Step(0);
        h.Step(1000,false);
        Check(!Field<bool>(h.Bar,"_isDocked"),"Re-reading one valid capture prematurely confirmed recovery.");
        h.Step(1,true);
        Check(Field<bool>(h.Bar,"_isDocked"),"Second independent stable capture did not restore docking.");
    }

    private static void InterruptedFailureTiming()
    {
        using var h=new Harness(taskbar:true);TaskbarDockSnapshot stable=h.Snapshot;
        for(int i=0;i<12;i++)
        {
            h.Snapshot=Insufficient(h);h.Step(0);h.Step(1750);
            Check(Field<bool>(h.Bar,"_isDocked"),"Short failure unexpectedly fell back.");
            h.Snapshot=stable;h.Step(350);
            Check(Field<bool>(h.Bar,"_isDocked"),"Good sample failed to reset accumulated failure time.");
        }
        h.Step(650);
        Check(h.Bar.Visible,"Stable valid samples did not recover after interrupted failures.");
    }

    private static void UnknownFailureTiming()
    {
        using var h=new Harness(taskbar:true);
        h.Snapshot=Transient(h,false);h.Step(0);h.Step(9999);
        Check(Field<bool>(h.Bar,"_isDocked")&&!h.Bar.Visible,"Unknown layout was treated as confirmed lack of room.");
        h.Step(1);
        Check(!Field<bool>(h.Bar,"_isDocked")&&h.Bar.Visible,"Persistent unknown layout did not provide its delayed fallback.");
    }

    private static void HiddenDuringTiming()
    {
        using var h=new Harness(taskbar:true);Call(h.Bar,"ToggleVisibility");
        h.Snapshot=Insufficient(h);h.Step(0);h.Step(2000);
        Check(!h.Bar.Visible&&Field<bool>(h.Bar,"_userHidden"),"Confirmed fallback revived a manually hidden bar.");
        h.Snapshot=AvailableSnapshot(h.Bar.DeviceDpi);h.Step(0);h.Step(650);
        Check(Field<bool>(h.Bar,"_isDocked")&&!h.Bar.Visible,"Stable redocking revived a manually hidden bar.");
        h.Snapshot=Transient(h,false);h.Step(0);h.Step(10000);
        Check(!h.Bar.Visible,"Unknown-layout timeout revived a manually hidden bar.");
        h.Snapshot=AvailableSnapshot(h.Bar.DeviceDpi) with {ShouldHide=true};h.Step(0);h.Step(15000);
        h.Snapshot=AvailableSnapshot(h.Bar.DeviceDpi);h.Step(0);h.Step(650);
        Check(!h.Bar.Visible&&Field<bool>(h.Bar,"_userHidden"),"Fullscreen recovery revived manual hide.");
    }

    private static void FullscreenDuringTiming()
    {
        using var h=new Harness(taskbar:true);
        h.Snapshot=Insufficient(h);h.Step(0);h.Step(1750);
        h.Snapshot=h.Snapshot with {ShouldHide=true};h.Step(0);
        Check(!h.Bar.Visible&&!Field<bool>(h.Bar,"_userHidden"),"Fullscreen did not hide immediately or modified user intent.");
        h.Step(15000);
        Check(Field<bool>(h.Bar,"_isDocked")&&!h.Bar.Visible,"Fullscreen time triggered fallback.");
        h.Snapshot=Insufficient(h);h.Step(0);h.Step(1999);
        Check(Field<bool>(h.Bar,"_isDocked"),"Pre-fullscreen failure time leaked into a new confirmation period.");
        h.Step(1);
        Check(!Field<bool>(h.Bar,"_isDocked"),"Post-fullscreen failure did not use a fresh two-second interval.");
    }

    private static void TargetJitter()
    {
        using var h=new Harness(taskbar:true);TaskbarDockSnapshot stable=h.Snapshot;Rectangle original=h.Bar.Bounds;
        Rectangle bigger=stable.AvailableArea;bigger.Width+=12;
        TaskbarDockSnapshot shifted=stable with {AvailableArea=bigger};
        int moves=0;h.Bar.LocationChanged+=(_,_)=>moves++;
        for(int i=0;i<20;i++)
        {
            h.Snapshot=shifted;h.Step(350);Check(h.Bar.Bounds==original,"One changed sample moved the bar.");
            h.Snapshot=stable;h.Step(350);
        }
        Check(moves==0,"Short target jitter moved the bar.");
        h.Snapshot=shifted;h.Step(0);h.Step(649);
        Check(h.Bar.Bounds==original,"Changed anchor committed before stability threshold.");
        h.Step(1);
        Check(h.Bar.Left==original.Left+12&&h.Bar.Size==original.Size,"Stable shifted anchor did not commit exactly once.");
        Check(moves==1,"Stable anchor update caused repeated moves.");
    }
}
