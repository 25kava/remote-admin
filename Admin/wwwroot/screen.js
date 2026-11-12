const ScreenGeometry = {
  point(x,y,rect,width,height){
    if(![x,y,rect.width,rect.height,width,height].every(Number.isFinite)||rect.width<=0||rect.height<=0||width<=0||height<=0)return null;
    const scale=Math.min(rect.width/width,rect.height/height), w=width*scale, h=height*scale;
    const left=rect.left+(rect.width-w)/2, top=rect.top+(rect.height-h)/2;
    if(x<left||x>left+w||y<top||y>top+h)return null;
    return {x:Math.max(0,Math.min(1,(x-left)/w)),y:Math.max(0,Math.min(1,(y-top)/h))};
  },
  wheel(delta,mode){return Math.max(-1200,Math.min(1200,Math.round(-delta*(mode===1?40:mode===2?120:1.2))));}
};
if(typeof module!=='undefined')module.exports=ScreenGeometry;

// Input is bound to this browser's visible screen session and fullscreen view.
const ScreenView = typeof window==='undefined'?null:(() => {
  let root=null,send=null,supported=false,controlSupported=false,connected=false,requested=false,streaming=false;
  let lastFrame=0,lastSequence=0,frameWidth=0,frameHeight=0,lastMove=0,lastPoint=null;
  let fpsStarted=0,fpsFrames=0,measuredFps=0;
  let controlRequested=false,controlling=false,inputBlocked=false,blockPending=false;
  const el=id=>root?.querySelector('#'+id);
  const fullscreen=()=>!!root&&document.fullscreenElement===el('screenViewport');
  function controlStatus(text){if(el('controlStatus'))el('controlStatus').textContent=text;}
  function buttons(){
    if(!el('screenStart'))return;
    el('screenStart').disabled=!supported||!connected||requested;
    el('screenStop').disabled=!requested;
    el('screenFullscreen').disabled=el('screenImage').hidden;
    el('controlStart').disabled=!controlSupported||!fullscreen()||!streaming||!connected||controlRequested;
    el('controlStop').disabled=!controlRequested;
    const block=el('blockInputs');
    if(block){block.hidden=!controlling;block.disabled=!controlling||blockPending;block.classList.toggle('active',inputBlocked);block.setAttribute('aria-pressed',String(inputBlocked));}
  }
  function stopControl(message='Remote control stopped.'){
    if(controlRequested)send?.({type:'controlStop'});
    controlRequested=false;controlling=false;inputBlocked=false;blockPending=false;lastPoint=null;
    el('screenViewport')?.classList.remove('controlling');
    controlStatus(message);buttons();
  }
  function clear(message){
    stopControl();requested=false;streaming=false;lastSequence=0;fpsStarted=0;fpsFrames=0;measuredFps=0;
    const image=el('screenImage');
    if(image){image.hidden=true;image.removeAttribute('src');el('screenPlaceholder').hidden=false;el('screenStatus').textContent=message;}
    buttons();
  }
  function stop(){stopControl();if(requested)send?.({type:'screenStop'});clear('Screen sharing stopped.');}
  function input(value){
    if(!controlling||!fullscreen()||!streaming||!connected||!document.hasFocus())return;
    send({type:'controlInput',payload:{fullscreen:true,input:value}});
  }
  function point(event){return ScreenGeometry.point(event.clientX,event.clientY,el('screenImage').getBoundingClientRect(),frameWidth,frameHeight);}
  function mount(content,canShare,sendMessage,canControl=false){
    stop();send=sendMessage;supported=canShare;controlSupported=canControl;connected=false;
    root=document.createElement('div');root.className='panel';
    root.innerHTML=`<div class="screenHeader"><h3>Live screen</h3><div><button id="screenStart" disabled>View screen</button><button id="screenStop" disabled>Stop viewing</button><button id="screenFullscreen" disabled>Full screen</button></div></div><div id="screenViewport" class="screenViewport" tabindex="0"><div class="screenFsBar"><button id="controlStart" disabled>Enable control</button><button id="controlStop" disabled>Stop control</button><button id="blockInputs" hidden aria-pressed="false" title="Block the target's physical mouse and keyboard">Block inputs</button><button id="screenExitFullscreen">Exit fullscreen</button><span id="controlStatus" role="status">View only. Enable control to use mouse and keyboard. Escape exits fullscreen.</span></div><img id="screenImage" draggable="false" alt="Live view of the client's primary display, including its mouse pointer" hidden><span id="screenPlaceholder" class="muted screenPlaceholder">The client shares its primary display silently.</span></div><p id="screenStatus" class="muted" role="status"></p>`;
    content.querySelector('h2').insertAdjacentElement('afterend',root);
    clear(supported?'Ready. Primary display · up to 30 fps.':'Update Client.exe on this computer to enable screen sharing.');
    el('screenStart').onclick=()=>{
      if(!supported||!connected||requested)return;
      clear('Starting the live view…');requested=true;buttons();send({type:'screenStart'});
    };
    el('screenStop').onclick=stop;
    el('screenFullscreen').onclick=async()=>{
      try{await el('screenViewport').requestFullscreen();buttons();controlStatus(controlSupported?'View only. Enable control to use mouse and keyboard.':'Control requires an updated client on a different computer.');}
      catch{el('screenStatus').textContent='Fullscreen is not available in this browser.';}
    };
    el('screenExitFullscreen').onclick=()=>document.exitFullscreen?.();
    el('controlStart').onclick=()=>{
      if(!fullscreen()||!streaming||!controlSupported||controlRequested)return;
      controlRequested=true;controlStatus('Enabling remote control…');buttons();
      send({type:'controlStart',payload:{fullscreen:true}});
      send({type:'controlHeartbeat',payload:{fullscreen:true}});
    };
    el('controlStop').onclick=()=>stopControl();
    el('blockInputs').onclick=()=>{
      if(!controlling||blockPending||!fullscreen())return;
      blockPending=true;buttons();
      send({type:'controlBlock',payload:{fullscreen:true,blocked:!inputBlocked}});
      el('screenViewport').focus();
    };
    const viewport=el('screenViewport');
    viewport.addEventListener('pointermove',event=>{
      if(!controlling||!fullscreen())return;
      const now=performance.now();if(now-lastMove<33)return;
      const p=point(event);if(!p)return;
      lastMove=now;lastPoint=p;input({kind:'move',x:p.x,y:p.y,button:0,delta:0});
    });
    viewport.addEventListener('pointerdown',event=>{
      if(!controlling||!fullscreen()||event.target.closest('button')||event.button>2)return;
      const p=point(event);if(!p)return;
      event.preventDefault();viewport.focus();viewport.setPointerCapture(event.pointerId);lastPoint=p;
      input({kind:'buttonDown',x:p.x,y:p.y,button:event.button,delta:0});
    });
    viewport.addEventListener('pointerup',event=>{
      if(!controlling||event.button>2)return;
      const p=point(event)||lastPoint;if(p)input({kind:'buttonUp',x:p.x,y:p.y,button:event.button,delta:0});
      if(viewport.hasPointerCapture(event.pointerId))viewport.releasePointerCapture(event.pointerId);
    });
    viewport.addEventListener('pointercancel',()=>stopControl('Control stopped: pointer capture was interrupted.'));
    viewport.addEventListener('contextmenu',event=>{if(controlling)event.preventDefault();});
    viewport.addEventListener('keydown',event=>{
      if((event.code==='Escape'||event.key==='Escape')&&fullscreen()){
        stopControl('Control stopped: fullscreen ended.');
        document.exitFullscreen?.().catch(()=>{});
      }
    });
    viewport.addEventListener('wheel',event=>{
      if(!controlling||!fullscreen())return;
      const p=point(event);if(!p)return;
      event.preventDefault();input({kind:'wheel',x:p.x,y:p.y,button:0,delta:ScreenGeometry.wheel(event.deltaY,event.deltaMode)});
    },{passive:false});
    for(const [eventName,kind] of [['keydown','keyDown'],['keyup','keyUp']])
      viewport.addEventListener(eventName,event=>{
        if(!controlling||!fullscreen()||event.code==='Escape'||event.target.closest('button'))return;
        event.preventDefault();input({kind,key:event.code,x:0,y:0,button:0,delta:0});
      });
    viewport.addEventListener('dragstart',event=>event.preventDefault());
    buttons();
  }
  function handle(message){
    if(message.type==='controlBlockState'){
      if(!controlRequested)return true;
      const p=message.payload;
      inputBlocked=!!p.blocked;blockPending=false;
      controlStatus(p.message||(inputBlocked?'Target mouse and keyboard are blocked.':'Target input is unblocked.'));
      buttons();
      return true;
    }
    if(message.type==='controlState'){
      if(!controlRequested)return true;
      const p=message.payload;
      if(p.status==='active'&&fullscreen()&&streaming&&connected){
        controlling=true;inputBlocked=false;blockPending=false;
        el('screenViewport').classList.add('controlling');el('screenViewport').focus();
        controlStatus('Remote control active · Escape exits · Block inputs locks the target mouse and keyboard.');buttons();
      }else stopControl(p.message||'Remote control stopped.');
      return true;
    }
    if(message.type!=='screenState'&&message.type!=='screenFrame')return false;
    if(!requested||!el('screenImage'))return true;
    const p=message.payload;
    if(message.type==='screenState'){
      if(p.status==='streaming'){
        streaming=true;lastFrame=Date.now();fpsStarted=lastFrame;fpsFrames=0;measuredFps=0;el('screenStatus').textContent='Live · waiting for first frame…';
      }else if(p.status==='starting')el('screenStatus').textContent=p.message;
      else clear(p.message||'Screen sharing stopped.');
    }else if(streaming){
      if(typeof p.jpegBase64!=='string'||p.jpegBase64.length>2000000||p.sequence<=lastSequence)return true;
      lastFrame=Date.now();lastSequence=p.sequence;frameWidth=p.width;frameHeight=p.height;
      fpsFrames++;const fpsElapsed=lastFrame-fpsStarted;
      if(fpsElapsed>=1000){measuredFps=fpsFrames*1000/fpsElapsed;fpsStarted=lastFrame;fpsFrames=0;}
      const image=el('screenImage');image.src='data:image/jpeg;base64,'+p.jpegBase64;image.hidden=false;
      el('screenPlaceholder').hidden=true;
      el('screenStatus').textContent=`Live · ${p.width} × ${p.height} · ${measuredFps?measuredFps.toFixed(1)+' fps':'measuring fps…'} · frame ${p.sequence} · ${new Date(p.capturedAtUtc).toLocaleTimeString()}`;buttons();
    }
    return true;
  }
  function connectionChanged(open){connected=open;if(!open)clear('Connection closed.');buttons();}
  function deviceChanged(device){supported=!!device?.screenStreamingSupported;controlSupported=!!device?.remoteControlSupported;if(!device)clear('Client disconnected.');buttons();}
  document.addEventListener('fullscreenchange',()=>{if(!fullscreen())stopControl('Control stopped: fullscreen ended.');buttons();});
  window.addEventListener('blur',()=>stopControl('Control stopped: the Admin window lost focus.'));
  document.addEventListener('visibilitychange',()=>{if(document.hidden)stopControl('Control stopped: the Admin tab is hidden.');});
  window.addEventListener('pagehide',stop);
  setInterval(()=>{
    if(streaming&&Date.now()-lastFrame>5000){stop();clear('No recent frames. Start a new request to try again.');}
    if(controlRequested){if(fullscreen()&&document.hasFocus()&&streaming&&connected)send?.({type:'controlHeartbeat',payload:{fullscreen:true}});else stopControl();}
  },500);
  return {mount,handle,stop,connectionChanged,deviceChanged};
})();
