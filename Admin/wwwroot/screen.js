// One screen subscription belongs to the currently selected device/browser socket.
const ScreenView = (() => {
  let root=null, send=null, supported=false, connected=false, requested=false, streaming=false;
  let lastFrame=0, lastSequence=0;
  const el=id=>root?.querySelector('#'+id);

  function buttons(){
    if(!el('screenStart'))return;
    el('screenStart').disabled=!supported||!connected||requested;
    el('screenStop').disabled=!requested;
    el('screenFullscreen').disabled=el('screenImage').hidden;
  }
  function clear(message){
    requested=false;streaming=false;lastSequence=0;
    const image=el('screenImage');
    if(image){image.hidden=true;image.removeAttribute('src');el('screenPlaceholder').hidden=false;el('screenStatus').textContent=message;}
    buttons();
  }
  function stop(){if(requested)send?.({type:'screenStop'});clear('Screen sharing stopped.');}
  function mount(content,canShare,sendMessage){
    stop();send=sendMessage;supported=canShare;connected=false;
    root=document.createElement('div');root.className='panel';
    root.innerHTML=`<div class="screenHeader"><h3>Live screen</h3><div><button id="screenStart" disabled>View screen</button><button id="screenStop" disabled>Stop viewing</button><button id="screenFullscreen" disabled>Full screen</button></div></div><div id="screenViewport" class="screenViewport"><img id="screenImage" alt="Live view of the client's primary display" hidden><span id="screenPlaceholder" class="muted screenPlaceholder">The client displays a sharing indicator and a Stop sharing button.</span></div><p id="screenStatus" class="muted" role="status"></p>`;
    content.querySelector('h2').insertAdjacentElement('afterend',root);
    clear(supported?'Ready. Primary display · up to 5 fps.':'Update Client.exe on this computer to enable screen sharing.');
    el('screenStart').onclick=()=>{
      if(!supported||!connected||requested)return;
      clear('Starting the live view…');requested=true;buttons();
      send({type:'screenStart'});
    };
    el('screenStop').onclick=stop;
    el('screenFullscreen').onclick=()=>el('screenViewport').requestFullscreen?.().catch(()=>{});
  }
  function handle(message){
    if(message.type!=='screenState'&&message.type!=='screenFrame')return false;
    if(!requested||!el('screenImage'))return true;
    const p=message.payload;
    if(message.type==='screenState'){
      if(p.status==='streaming'){
        streaming=true;lastFrame=Date.now();el('screenStatus').textContent='Live · waiting for first frame…';
      }else if(p.status==='starting')el('screenStatus').textContent=p.message;
      else clear(p.message||'Screen sharing stopped.');
    }else if(streaming){
      if(typeof p.jpegBase64!=='string'||p.jpegBase64.length>2000000||p.sequence<=lastSequence)return true;
      lastFrame=Date.now();lastSequence=p.sequence;
      const image=el('screenImage');image.src='data:image/jpeg;base64,'+p.jpegBase64;image.hidden=false;
      el('screenPlaceholder').hidden=true;
      el('screenStatus').textContent=`Live · ${p.width} × ${p.height} · frame ${p.sequence} · ${new Date(p.capturedAtUtc).toLocaleTimeString()}`;
      buttons();
    }
    return true;
  }
  function connectionChanged(open){connected=open;if(!open)clear('Connection closed.');buttons();}
  function deviceChanged(device){
    supported=!!device?.screenStreamingSupported;
    if(!device)clear('Client disconnected.');
    buttons();
  }
  window.addEventListener('pagehide',stop);
  setInterval(()=>{if(streaming&&Date.now()-lastFrame>5000){stop();clear('No recent frames. Start a new request to try again.');}},1000);
  return {mount,handle,stop,connectionChanged,deviceChanged};
})();
