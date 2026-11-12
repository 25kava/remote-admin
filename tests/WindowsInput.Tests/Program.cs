using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteAdmin.Shared;

var count = 0;
void Assert(bool value,string message){if(!value)throw new Exception(message);count++;}
var batches = new List<WindowsInputSink.NativeInput[]>();
var available = true;
var sink = new WindowsInputSink(inputs=>{batches.Add(inputs);return (uint)inputs.Length;},()=>available,
    ()=>(new Rectangle(0,0,1920,1080),new Rectangle(-1920,0,3840,1080)));
Assert(Marshal.SizeOf<WindowsInputSink.NativeInput>()==40,"Wrong x64 SendInput structure size");
sink.Apply(new("keyDown",0,0,0,0,"ControlRight"));
var key=batches.Last().Single();
Assert(key.Type==1&&key.Data.Keyboard.Scan==0x1d&&key.Data.Keyboard.Flags==0x9,"Right Ctrl must use an extended hardware scan code");
sink.Apply(new("keyUp",0,0,0,0,"ControlRight"));
Assert(batches.Last()[0].Data.Keyboard.Flags==0xb,"Key-up flags missing");
var before=batches.Count;sink.Apply(new("keyUp",0,0,0,0,"ControlLeft"));
Assert(batches.Count==before,"Released a key the remote session never pressed");
sink.Apply(new("buttonDown",1,1,2,0,null));
var mouse=batches.Last().Single().Data.Mouse;
Assert(mouse.X==65535&&mouse.Y==65535&&(mouse.Flags&0xc009)==0xc009,"Absolute right-click or virtual desktop flags incorrect");
sink.Apply(new("wheel",.5,.5,0,-120,null));
Assert(unchecked((int)batches.Last()[0].Data.Mouse.Data)==-120,"Mouse wheel sign was lost");
sink.Apply(new("keyDown",0,0,0,0,"ShiftLeft"));
sink.ReleaseAll();
var releases=batches.Last();
Assert(releases.Length==2,"Did not release held key and right mouse button");
Assert(releases.Any(x=>x.Type==0&&x.Data.Mouse.Flags==0x10),"Mouse-up was not sent");
Assert(releases.Any(x=>x.Type==1&&(x.Data.Keyboard.Flags&2)!=0),"Key-up was not sent");
before=batches.Count;sink.ReleaseAll();Assert(batches.Count==before,"ReleaseAll retained stale input state");
available=false;
try{sink.Apply(new("keyDown",0,0,0,0,"KeyA"));throw new Exception("Secure desktop allowed input");}
catch(InvalidOperationException){Assert(batches.Count==before,"Injected into an unavailable desktop");}

// Exercise cursor drawing against a synthetic canvas only. Do not move/click/type on the user's desktop.
using var bitmap=new Bitmap(160,160);
using(var graphics=Graphics.FromImage(bitmap))
{
    graphics.Clear(Color.RoyalBlue);
    DesktopCursor.DrawCursor(graphics,Cursors.Arrow.Handle,new Point(40,40),new Rectangle(0,0,160,160));
}
var changed=0;
for(var y=0;y<160;y++)for(var x=0;x<160;x++)if(bitmap.GetPixel(x,y).ToArgb()!=Color.RoyalBlue.ToArgb())changed++;
Assert(changed>0,"Native cursor was not drawn into the synthetic canvas");
Assert(WindowsInputBlocker.IsInjected(0x10,keyboard:true),"Injected keyboard input was not recognized");
Assert(!WindowsInputBlocker.IsInjected(0,keyboard:true),"Physical keyboard input was treated as injected");
Assert(WindowsInputBlocker.IsInjected(0x01,keyboard:false),"Injected mouse input was not recognized");
Assert(!WindowsInputBlocker.IsInjected(0x10,keyboard:false),"Keyboard injected flag affected the mouse filter");
bitmap.Save(Path.Combine(AppContext.BaseDirectory,"cursor-on-synthetic-canvas.png"),ImageFormat.Png);
Console.WriteLine($"PASS {count} native encoding and cursor assertions; no native mouse/keyboard input was injected.");

internal static class DesktopAccess
{
    public static bool IsInteractiveDesktop()=>false; // Prevent actual injection if a test omits its fake API.
}
