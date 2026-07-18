// 2D hierarchical LAN topology diagram - Canvas 2D renderer.
// Subscribes to lan-flow-data.js (published by the 3D map) so there are
// zero duplicate API calls. GPU-composited canvas for smooth particle animation.

// KEEP IN SYNC: lan-flow-map.js imports the same module. Both must use the same ?v= or they get separate instances.
import * as flowData from './lan-flow-data.js?v=5';

function demoMask(text) {
    const dm = window.DemoMask;
    return (dm?.isEnabled() && dm.maskString) ? (dm.maskString(text) ?? text) : text;
}

// ---- Color palette (matches 3D map) ----
const C = {
    bg:           '#202023',
    gateway:      '#facc15',
    switchNode:   '#9aa6b2',
    ap:           '#3385d6',
    wiredClient:  '#c9d2e0',
    wifiClient:   '#e2e8f0',
    cloud:        '#4d556b',
    virtualHub:   '#6b7785',
    downstream:   '#3385d6',
    upstream:     '#24bc70',
    pipeCool:     '#1f4068',
    pipeWarm:     '#e79613',
    pipeHot:      '#ee6368',
    band24:       '#fbbf24',
    band5:        '#3b82f6',
    band6:        '#a855f7',
    text:         '#f1f5f9',
    textSec:      '#cbd5e1',
    textMuted:    '#9ca3af',
    labelBg:      'rgba(16,24,32,0.82)',
    cardBg:       '#1a1d23',
    globeStroke:  '#3b82f6',
};

const NK = { Gateway:0, Switch:1, AP:2, WiredClient:3, WifiClient:4, Cloud:5, VirtualHub:6 };
const LK = { Uplink:0, WiredClient:1, WifiClient:2, Wan:3, Transit:4, MeshBackhaul:5 };
const CT = { Solid:0, PathProxy:1, Unresolved:2 }; // LanCloudTier

// ---- Layout geometry ----
const G = {
    tierGap:     170,
    clientTierGap:165,
    cloudGap:    220,
    infraGap:    90,
    clientCellW: 145,
    clientCellH: 55,
    clientR:     7,
    clientCols:  6,
    maxClients:  80,
    iconSize:    52,
    boxW:        68,
    boxH:        60,
    cloudR:      30,
    cornerR:     12,
    pad:         80,
    pipeBase:    2,
    pipeMax:     6,
    nameFont:    11,
    rateFont:    10,
    clientFont:  9,
};

const RATE_THRESH = 500_000;
const EMIT_MAX = 12;
const MAX_DOTS = 80;
const FONT = 'system-ui, -apple-system, sans-serif';

// ---- Helpers ----

function hexRgb(h) { return [parseInt(h.slice(1,3),16), parseInt(h.slice(3,5),16), parseInt(h.slice(5,7),16)]; }
function rgbHex(r,g,b) { return '#'+[r,g,b].map(c=>Math.round(Math.max(0,Math.min(255,c))).toString(16).padStart(2,'0')).join(''); }
function lerp(a,b,t) { return a+(b-a)*t; }
function lerpColor(a,b,t) { const [r1,g1,b1]=hexRgb(a),[r2,g2,b2]=hexRgb(b); return rgbHex(lerp(r1,r2,t),lerp(g1,g2,t),lerp(b1,b2,t)); }
function bandClr(b) { return b==='2.4'?C.band24:b==='5'?C.band5:b==='6'?C.band6:null; }
function nodeClr(k,b) {
    if(k===NK.Gateway)return C.gateway; if(k===NK.Switch)return C.switchNode;
    if(k===NK.AP)return C.ap; if(k===NK.WiredClient)return C.wiredClient;
    if(k===NK.WifiClient)return bandClr(b)||C.wifiClient;
    if(k===NK.Cloud)return C.cloud; if(k===NK.VirtualHub)return C.virtualHub;
    return C.textMuted;
}
function isInfra(k) { return k<=NK.AP||k===NK.VirtualHub; }
function isClient(k) { return k===NK.WiredClient||k===NK.WifiClient; }

// Duration of the fade-in applied to a client that gets re-attached to a different
// AP during historic playback (roam), so it doesn't pop at its new position.
const ROAM_FADE_MS = 350;

function pipeClr(u,band) {
    const cool=bandClr(band)||C.pipeCool;
    if(u<0.7)return cool;
    if(u<0.9)return lerpColor(cool,C.pipeWarm,(u-0.7)/0.2);
    return lerpColor(C.pipeWarm,C.pipeHot,Math.min((u-0.9)/0.1,1));
}
function pipeW(cap) {
    if(!cap||cap<=0)return G.pipeBase;
    const t=Math.log10(Math.max(cap/1e9,0.01))+2;
    return G.pipeBase+Math.min(t,3.5)*0.9;
}

function formatBps(bps) {
    if(!Number.isFinite(bps)||bps<=0)return '0 bps';
    const u=['bps','Kbps','Mbps','Gbps','Tbps'];
    let i=0,v=bps;
    while(v>=1000&&i<u.length-1){v/=1000;i++;}
    return `${v>=100?v.toFixed(0):v.toFixed(1)} ${u[i]}`;
}
function formatSpeed(mbps) {
    if(!mbps)return '';
    if(mbps>=1000){const g=mbps/1000;return `${g%1===0?g.toFixed(0):g.toFixed(1)} Gbps`;}
    return `${mbps} Mbps`;
}

function withAlpha(hex, a) {
    const [r,g,b] = hexRgb(hex);
    return `rgba(${r},${g},${b},${a})`;
}

// ---- Orthogonal path helpers ----
// All accept an optional midYOff to vertically stagger sibling links.

function orthoLen(x1,y1,x2,y2,midYOff) {
    if(Math.abs(x1-x2)<0.5)return Math.abs(y2-y1);
    const my=(y1+y2)/2+(midYOff||0);
    const top=my-y1, bot=y2-my;
    const ax=Math.abs(x2-x1),cr=Math.min(G.cornerR,ax/2,Math.min(top,bot));
    if(cr<1)return top+ax+bot;
    return (top-cr)+(bot-cr)+Math.PI*cr+(ax-2*cr);
}

function orthoAt(x1,y1,x2,y2,t,midYOff) {
    if(Math.abs(x2-x1)<0.5)return{x:x1,y:y1+(y2-y1)*t};
    const midY=(y1+y2)/2+(midYOff||0);
    const dx=x2-x1,s=dx>0?1:-1,ax=Math.abs(dx);
    const top=midY-y1,bot=y2-midY;
    const cr=Math.min(G.cornerR,ax/2,Math.min(top,bot));
    if(cr<1){const tot=top+ax+bot;let d=t*tot;if(d<=top)return{x:x1,y:y1+d};d-=top;if(d<=ax)return{x:x1+s*d,y:midY};d-=ax;return{x:x2,y:midY+d};}
    const s1=top-cr,s2=Math.PI/2*cr,s3=ax-2*cr,s4=s2,s5=bot-cr;
    const tot=s1+s2+s3+s4+s5;let d=t*tot;
    if(d<=s1)return{x:x1,y:y1+d};d-=s1;
    if(d<=s2){
        // Quadratic bezier matching strokeOrtho: P0=(x1,midY-cr) P1=(x1,midY) P2=(x1+s*cr,midY)
        const bt=d/s2,u=1-bt;
        return{x:u*u*x1+2*u*bt*x1+bt*bt*(x1+s*cr), y:u*u*(midY-cr)+2*u*bt*midY+bt*bt*midY};
    }d-=s2;
    if(d<=s3)return{x:x1+s*(cr+d),y:midY};d-=s3;
    if(d<=s4){
        // Quadratic bezier: P0=(x2-s*cr,midY) P1=(x2,midY) P2=(x2,midY+cr)
        const bt=d/s4,u=1-bt;
        return{x:u*u*(x2-s*cr)+2*u*bt*x2+bt*bt*x2, y:u*u*midY+2*u*bt*midY+bt*bt*(midY+cr)};
    }d-=s4;
    return{x:x2,y:midY+cr+d};
}

function strokeOrtho(ctx,x1,y1,x2,y2,midYOff) {
    const r=G.cornerR;
    ctx.beginPath();
    if(Math.abs(x1-x2)<0.5){ctx.moveTo(x1,y1);ctx.lineTo(x2,y2);ctx.stroke();return;}
    const midY=(y1+y2)/2+(midYOff||0);
    const dx=x2-x1,s=dx>0?1:-1,ax=Math.abs(dx);
    const top=midY-y1,bot=y2-midY;
    const cr=Math.min(r,ax/2,Math.min(top,bot));
    ctx.moveTo(x1,y1);
    if(cr<1){ctx.lineTo(x1,midY);ctx.lineTo(x2,midY);ctx.lineTo(x2,y2);ctx.stroke();return;}
    ctx.lineTo(x1,midY-cr);
    ctx.quadraticCurveTo(x1,midY,x1+s*cr,midY);
    ctx.lineTo(x2-s*cr,midY);
    ctx.quadraticCurveTo(x2,midY,x2,midY+cr);
    ctx.lineTo(x2,y2);
    ctx.stroke();
}

// ---- Layout tree ----
class TN {
    constructor(d){this.d=d;this.infra=[];this.clients=[];this.x=0;this.y=0;this.w=0;}
}

// ---- Particle stream (no DOM, just state) ----
class Stream {
    constructor(edge,dir,color){
        this.edge=edge; this.dir=dir; this.color=color;
        this.density=0; this.velNorm=0; this.dotSize=0.4;
        this.spawnAcc=0;
        this.midYOff=edge._midYOff||0;
        this.pathLen=orthoLen(edge._x1,edge._y1,edge._x2,edge._y2,this.midYOff);
        this.slots=[];
        for(let i=0;i<MAX_DOTS;i++)this.slots.push({t:-1,size:0});
        this._seeded=false;
    }
    setRate(bps){
        const intensity=Math.max(0,Math.min(1,Math.log10(Math.max(bps,1))/11));
        this.density=intensity;
        this.dotSize=0.4+(intensity*intensity)*1.2;
        // Constant visual speed: match 3D map (2.5 + intensity*4 scene-units/sec)
        // scaled to SVG pixels. Divide by path length for normalized t/sec.
        const absPxSec=30+intensity*50;
        this.velNorm=absPxSec/Math.max(this.pathLen,1);

        // Pre-seed on first non-zero rate so the pipe is already populated
        // instead of building up from empty over the first few seconds.
        if(!this._seeded&&intensity>0){
            this._seeded=true;
            const emitPerSec=intensity*intensity*EMIT_MAX;
            const traverseTime=1/Math.max(this.velNorm,0.001);
            const steadyState=Math.min(Math.round(emitPerSec*traverseTime),MAX_DOTS);
            for(let i=0;i<steadyState;i++){
                this.slots[i].t=(i+0.5)/steadyState;
                if(this.dir<0)this.slots[i].t=1-this.slots[i].t;
                this.slots[i].size=this.dotSize;
            }
        }
    }
    advance(dt){
        // Emission: density²*12 dots/sec, jittered ±20% (3D uses ±40% but
        // shorter 2D links need tighter spacing to avoid visible gaps)
        this.spawnAcc+=this.density*this.density*EMIT_MAX*dt;
        while(this.spawnAcc>=1){
            this.spawnAcc-=(0.8+Math.random()*0.4);
            for(const sl of this.slots){if(sl.t<0){sl.t=this.dir>0?0:1;sl.size=this.dotSize;break;}}
        }
        for(const sl of this.slots){
            if(sl.t<0)continue;
            sl.t+=this.velNorm*dt*this.dir;
            if(sl.t>1||sl.t<0){sl.t=-1;continue;}
        }
    }
}

// ---- Main class ----
class LanFlowMap2D {
    constructor(container,opts){
        this._el=container;
        this._storageKey=(opts?.storagePrefix||'lanFlowMap2d')+'Overlays';
        this._canvas=null;
        this._ctx=null;
        this._dpr=1;
        this._cw=0; this._ch=0;

        this._root=null;
        this._treeMap=new Map();
        this._clouds=[];
        this._edges=[];
        this._liveRates={};
        this._streams=[];
        this._images=new Map();

        this._animId=0;
        this._lastFrame=0;
        this._unsub=null;
        this._needsStaticRedraw=true;
        this._staticCanvas=null;

        // Pan/zoom: offset in world coords + scale
        this._ox=0; this._oy=0; this._scale=1;
        this._isFitted=false;
        this._dragging=false; this._dragStart=null;
        // Multi-touch pinch zoom
        this._pointers=new Map();
        this._pinchStartDist=0;
        this._pinchStartScale=1;
        this._pinchStartCenter=null;
        this._pinchStartOx=0;
        this._pinchStartOy=0;
        // World bounds
        this._bx=0; this._by=0; this._bw=800; this._bh=600;

        this._tooltip=null;
        this._hoverNode=null;
        this._liveOnly=false;
    }

    async start(){
        if(flowData.isPaused())flowData.publishPlayState(false,'live');
        this._createCanvas();
        const snap=flowData.getSnapshot();
        if(snap){
            this._liveRates={...flowData.getLiveRates()};
            this._buildLayout(snap);
            await this._loadImages(snap);
            this._fitAll();
            this._needsStaticRedraw=true;
        }
        this._unsub=flowData.subscribe((ev)=>{
            if(ev==='snapshot'){
                const s=flowData.getSnapshot();
                if(s){
                    this._liveRates={...flowData.getLiveRates()};
                    if(!this._root){
                        // First load: full build + fit
                        this._buildLayout(s);
                        this._loadImages(s).then(()=>{this._fitAll();this._needsStaticRedraw=true;});
                    }else{
                        // Detect infrastructure change (devices, not just client churn)
                        const infraCount=(nodes)=>(nodes||[]).filter(n=>n.kind<=3||n.kind===6).length;
                        const prevInfra=infraCount(this._snapshot?.nodes);
                        const newInfra=infraCount(s.nodes);
                        if(newInfra!==prevInfra){
                            this._buildLayout(s);
                            this._loadImages(s).then(()=>{this._needsStaticRedraw=true;});
                        }else{
                            // Client churn or same topology: update data in place
                            this._updateSnapshotData(s);
                            this._snapshot=s;
                            this._needsStaticRedraw=true;
                        }
                    }
                }
            }else if(ev==='live'){
                Object.assign(this._liveRates,flowData.getLiveRates());
                this._applyClientAssoc();
                this._updateStreamRates();
                this._updateCloudStats();
                this._needsStaticRedraw=true;
            }else if(ev==='scrubber'||ev==='playstate'){
                this._syncScrubber();
            }else if(ev==='scrubber-window'){
                this._syncScrubberWindow();
            }
        });
        this._lastFrame=performance.now();
        this._animate();
    }

    dispose(){
        cancelAnimationFrame(this._animId);
        if(this._unsub)this._unsub();
        if(this._resizeObs)this._resizeObs.disconnect();
        if(this._onKeyDown)document.removeEventListener('keydown',this._onKeyDown);
        if(this._isFullscreen)this._el.classList.remove('lan-flow-map-fullscreen');
        this._streams=[];
        // The mobile scrubber lives outside _el (below the stage), so clearing
        // _el's children alone would leave it behind on unmount.
        if(this._scrubberMq)this._scrubberMq.removeEventListener('change',this._placeScrubber);
        if(this._scrubberEl)this._scrubberEl.remove();
        this._el.innerHTML='';
    }

    // ---- Canvas setup ----

    _createCanvas(){
        this._el.innerHTML='';
        const canvas=document.createElement('canvas');
        canvas.className='lfm2d-canvas';
        canvas.style.display='block';
        canvas.style.cursor='grab';
        canvas.style.touchAction='none';
        this._el.appendChild(canvas);
        this._canvas=canvas;
        this._ctx=canvas.getContext('2d');

        this._staticCanvas=document.createElement('canvas');

        // Tooltip div
        this._tooltip=document.createElement('div');
        this._tooltip.className='lfm2d-tooltip';
        this._el.appendChild(this._tooltip);

        // Fullscreen button (top-right, matching 3D map style)
        const fsBtn=document.createElement('button');
        fsBtn.className='lan-flow-map-fullscreen-btn';
        fsBtn.setAttribute('data-tooltip','Fullscreen');
        fsBtn.setAttribute('data-tooltip-hover-only','');
        fsBtn.innerHTML=`<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="3 8 3 3 8 3"></polyline><polyline points="16 3 21 3 21 8"></polyline>
            <polyline points="21 16 21 21 16 21"></polyline><polyline points="8 21 3 21 3 16"></polyline></svg>`;
        fsBtn.addEventListener('click',()=>this._toggleFullscreen());
        this._el.appendChild(fsBtn);
        this._fsBtn=fsBtn;

        // Esc key exits fullscreen
        this._onKeyDown=(e)=>{if(e.key==='Escape'&&this._isFullscreen)this._toggleFullscreen();};
        document.addEventListener('keydown',this._onKeyDown);

        // Filter + overlay state
        this._filter={text:'',bands:{'2.4':true,'5':true,'6':true}};
        const defaultOverlays={wifiClients:true,wiredClients:true,clouds:true};
        try{const s=JSON.parse(localStorage.getItem(this._storageKey));this._overlays=s?{...defaultOverlays,...s}:{...defaultOverlays};}
        catch{this._overlays={...defaultOverlays};}

        // Filter panel (top-left, matching 3D style, collapsible on mobile)
        const isMobile=window.matchMedia('(max-width: 768px)').matches;
        const filter=document.createElement('div');
        filter.className='lan-flow-map-panel lan-flow-map-filter';
        const filterTitle=document.createElement('div');
        filterTitle.className='lan-flow-map-panel-title';
        filterTitle.textContent=isMobile?'Filter':'Filter clients';
        if(isMobile)filterTitle.classList.add('lan-flow-map-panel-title-toggle');
        filter.appendChild(filterTitle);
        const filterBody=document.createElement('div');
        filterBody.className='lan-flow-map-panel-body';
        filterBody.innerHTML=`<input class="lan-flow-map-search" type="search" placeholder="Search by name or MAC" />
                <div class="lan-flow-map-chips" data-chip-group="band">
                    <span class="lan-flow-map-chip is-on" data-band="2.4">2.4 GHz</span>
                    <span class="lan-flow-map-chip is-on" data-band="5">5 GHz</span>
                    <span class="lan-flow-map-chip is-on" data-band="6">6 GHz</span>
                </div>`;
        if(isMobile)filterBody.classList.add('is-collapsed');
        filter.appendChild(filterBody);
        if(isMobile)filterTitle.addEventListener('click',()=>{filterBody.classList.toggle('is-collapsed');});
        filterBody.querySelector('.lan-flow-map-search').addEventListener('input',(e)=>{
            this._filter.text=(e.target.value||'').toLowerCase().trim();
            this._relayout();
            if(this._isFitted)this._fitAll();
        });
        const bandChips=[...filterBody.querySelectorAll('.lan-flow-map-chip')];
        bandChips.forEach(chip=>{
            chip.addEventListener('click',()=>{
                const b=chip.dataset.band;
                const allOn=bandChips.every(c=>this._filter.bands[c.dataset.band]);
                if(allOn){for(const c of bandChips){this._filter.bands[c.dataset.band]=(c.dataset.band===b);c.classList.toggle('is-on',this._filter.bands[c.dataset.band]);}}
                else{const onlyThis=this._filter.bands[b]&&bandChips.every(c=>c.dataset.band===b||!this._filter.bands[c.dataset.band]);
                    if(onlyThis){for(const c of bandChips){this._filter.bands[c.dataset.band]=true;c.classList.add('is-on');}}
                    else{this._filter.bands[b]=!this._filter.bands[b];chip.classList.toggle('is-on',this._filter.bands[b]);}}
                this._relayout();
                if(this._isFitted)this._fitAll();
            });
        });
        this._el.appendChild(filter);

        // Overlays panel (top-right, matching 3D style, collapsible on mobile)
        const controls=document.createElement('div');
        controls.className='lan-flow-map-panel lan-flow-map-controls';
        const ctrlTitle=document.createElement('div');
        ctrlTitle.className='lan-flow-map-panel-title';
        ctrlTitle.textContent='Overlays';
        if(isMobile)ctrlTitle.classList.add('lan-flow-map-panel-title-toggle');
        controls.appendChild(ctrlTitle);
        const ctrlBody=document.createElement('div');
        ctrlBody.className='lan-flow-map-panel-body';
        if(isMobile)ctrlBody.classList.add('is-collapsed');
        controls.appendChild(ctrlBody);
        if(isMobile)ctrlTitle.addEventListener('click',()=>{ctrlBody.classList.toggle('is-collapsed');});
        for(const[key,label]of[['wifiClients','Wi-Fi clients'],['wiredClients','Wired clients'],['clouds','WAN globes']]){
            const row=document.createElement('div');
            row.className=`lan-flow-map-toggle ${this._overlays[key]?'is-on':''}`;
            row.innerHTML=`<span>${label}</span><span class="lan-flow-map-toggle-pill"></span>`;
            row.addEventListener('click',()=>{
                this._overlays[key]=!this._overlays[key];
                row.classList.toggle('is-on',this._overlays[key]);
                try{localStorage.setItem(this._storageKey,JSON.stringify(this._overlays));}catch{}
                this._relayout();
                if(this._isFitted)this._fitAll();
            });
            ctrlBody.appendChild(row);
        }
        this._el.appendChild(controls);

        // Toolbar
        const tb=document.createElement('div');
        tb.className='lfm2d-toolbar';
        tb.innerHTML=`<button class="lfm2d-btn" data-action="zin" title="Zoom in">+</button>`
            +`<button class="lfm2d-btn" data-action="zout" title="Zoom out">&minus;</button>`
            +`<button class="lfm2d-btn" data-action="fit" title="Fit all"><svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="4 14 4 20 10 20"></polyline><polyline points="20 10 20 4 14 4"></polyline><line x1="14" y1="10" x2="20" y2="4"></line><line x1="4" y1="20" x2="10" y2="14"></line></svg></button>`;
        tb.addEventListener('click',(e)=>{
            const a=e.target.closest('[data-action]')?.dataset.action;
            if(a==='zin')this._zoomBy(1.3);
            else if(a==='zout')this._zoomBy(1/1.3);
            else if(a==='fit')this._fitAll();
        });
        this._el.appendChild(tb);

        // Controls help (starts collapsed, title click toggles - same pattern
        // as the Filter/Overlays panels on mobile, matching 3D style).
        // 2D subset of the 3D map's legend: no rotate, WASD, or move-device here.
        // Scrub/pause rows are omitted in liveOnly mounts (dashboard mini-map)
        // where the scrubber is hidden.
        const help=document.createElement('div');
        help.className='lan-flow-map-panel lan-flow-map-help';
        const helpTitle=document.createElement('div');
        helpTitle.className='lan-flow-map-panel-title lan-flow-map-panel-title-toggle';
        helpTitle.textContent='Controls';
        help.appendChild(helpTitle);
        const scrubRows=this._liveOnly?'':`
                <div class="lan-flow-map-help-row"><span>Pause / Play</span><span class="kbd">Space</span></div>
                <div class="lan-flow-map-help-row"><span>Scrub timeline</span><span class="kbd">←</span> <span class="kbd">→</span></div>
                <div class="lan-flow-map-help-row"><span>Fast scrub</span><span class="kbd">Shift</span> + <span class="kbd">←</span> <span class="kbd">→</span></div>`;
        const helpBody=document.createElement('div');
        helpBody.className='lan-flow-map-panel-body is-collapsed';
        helpBody.innerHTML=`
                <div class="lan-flow-map-help-row"><span>Pan</span><span class="kbd">Left-drag</span></div>
                <div class="lan-flow-map-help-row"><span>Zoom</span><span class="kbd">Scroll</span></div>
                <div class="lan-flow-map-help-row"><span>Hover detail</span><span class="kbd">Mouse over</span></div>
                <div class="lan-flow-map-help-row"><span>Open client</span><span class="kbd">Double-click</span></div>${scrubRows}
                <div class="lan-flow-map-help-row"><span>Fullscreen</span><span class="kbd">Esc</span> to exit</div>`;
        help.appendChild(helpBody);
        helpTitle.addEventListener('click',()=>helpBody.classList.toggle('is-collapsed'));
        this._el.appendChild(help);

        // Mode badge (bottom-left, matching 3D style)
        const status=document.createElement('div');
        status.className='lan-flow-map-panel lan-flow-map-status';
        const modeBadge=document.createElement('span');
        modeBadge.className='lan-flow-map-mode';
        modeBadge.textContent='Live';
        modeBadge.setAttribute('data-tooltip-hover-only','');
        modeBadge.addEventListener('click',()=>{
            const inst=window.__lanFlowMap?.getInstance?.();
            if(inst&&inst._mode==='historic')inst._returnToLive();
        });
        status.appendChild(modeBadge);
        this._el.appendChild(status);
        this._modeBadge=modeBadge;

        // Mirror scrubber (synced from 3D map via shared data store).
        // Interactions forward to the 3D map's instance.
        const scrubber=document.createElement('div');
        scrubber.className='lan-flow-map-scrubber';
        scrubber.innerHTML=`
            <div class="lan-flow-map-scrubber-row">
                <button class="lan-flow-map-scrubber-playpause" data-role="playpause" type="button" aria-label="Pause">⏸</button>
                <div class="lan-flow-map-speed-control" data-role="speed">
                    <button class="lan-flow-map-speed-step" data-dir="-1" type="button" aria-label="Slower">-</button>
                    <span class="lan-flow-map-speed-label" data-role="speed-label">1x</span>
                    <button class="lan-flow-map-speed-step" data-dir="1" type="button" aria-label="Faster">+</button>
                </div>
                <select class="lan-flow-map-scrubber-window" data-role="window" aria-label="Timeline range"></select>
                <span data-role="left">-24h</span>
                <span class="lan-flow-map-scrubber-track">
                    <input class="lan-flow-map-scrubber-range" type="range" min="0" max="10000" value="10000" />
                    <span class="lan-flow-map-scrubber-ticks" data-role="ticks"></span>
                </span>
                <span data-role="right">Live</span>
            </div>`;
        const windowSel=scrubber.querySelector('[data-role="window"]');
        for(const p of flowData.SCRUBBER_PRESETS){
            const opt=document.createElement('option');
            opt.value=p.key;opt.textContent=p.label;
            windowSel.appendChild(opt);
        }
        windowSel.value='24h';
        windowSel.addEventListener('change',()=>{
            const inst=window.__lanFlowMap?.getInstance?.();
            if(inst)inst._setScrubSpan(windowSel.value);
        });
        // Arrow left/right (with Shift for fast scrub) and Space float up to
        // timeline scrubbing and play/pause (on the 3D instance); without this
        // the focused dropdown consumes them natively.
        windowSel.addEventListener('keydown',(e)=>{
            const inst=window.__lanFlowMap?.getInstance?.();
            if(e.key==='Shift'){if(inst&&inst._keys)inst._keys.shift=true;return;}
            if(e.key===' '){
                e.preventDefault();
                if(inst)inst._togglePlayPause();
                return;
            }
            if(e.key!=='ArrowLeft'&&e.key!=='ArrowRight')return;
            e.preventDefault();
            if(inst&&inst._keys)inst._keys[e.key.toLowerCase()]=true;
        });
        windowSel.addEventListener('keyup',(e)=>{
            const inst=window.__lanFlowMap?.getInstance?.();
            if(!inst)return;
            if(e.key==='Shift'){if(inst._keys)inst._keys.shift=false;return;}
            if(e.key!=='ArrowLeft'&&e.key!=='ArrowRight')return;
            if(inst._keys)inst._keys[e.key.toLowerCase()]=false;
            inst._arrowScrubStart=null;
            inst._kbScrubTime=null;
        });
        // Forward all interactions to the 3D map
        const fwd=()=>window.__lanFlowMap?.getInstance?.();
        const sRange=scrubber.querySelector('.lan-flow-map-scrubber-range');
        sRange.addEventListener('pointerdown',()=>{
            const inst=fwd();if(inst)inst._stopHistoricPlayback?.();
        });
        sRange.addEventListener('keydown',(e)=>{
            e.preventDefault();
            const inst=fwd();if(!inst)return;
            if(e.key===' '){inst._togglePlayPause();return;}
            if(e.key==='Shift'){if(inst._keys)inst._keys.shift=true;return;}
            const key=e.key.toLowerCase();
            if(key==='arrowleft'||key==='arrowright'){
                if(inst._keys)inst._keys[key]=true;
            }
        });
        sRange.addEventListener('keyup',(e)=>{
            const inst=fwd();if(!inst)return;
            if(e.key==='Shift'){if(inst._keys)inst._keys.shift=false;return;}
            const key=e.key.toLowerCase();
            if(inst._keys)inst._keys[key]=false;
            if(e.key==='ArrowLeft'||e.key==='ArrowRight'){inst._arrowScrubStart=null;inst._kbScrubTime=null;}
        });
        sRange.addEventListener('input',(e)=>{
            const inst=fwd();if(inst){
                const r=inst._panels?.scrubberRange;
                if(r){r.value=e.target.value;r.dispatchEvent(new Event('input'));}
            }
        });
        sRange.addEventListener('change',(e)=>{
            const inst=fwd();if(inst){
                const r=inst._panels?.scrubberRange;
                if(r){r.value=e.target.value;r.dispatchEvent(new Event('change'));}
            }
        });
        scrubber.querySelector('[data-role="playpause"]').addEventListener('click',()=>{
            const inst=fwd();if(inst)inst._togglePlayPause();
        });
        for(const btn of scrubber.querySelectorAll('.lan-flow-map-speed-step')){
            btn.addEventListener('click',()=>{
                const inst=fwd();if(inst){
                    const ob=inst._panels?.scrubber?.querySelector(`.lan-flow-map-speed-step[data-dir="${btn.dataset.dir}"]`);
                    if(ob)ob.click();
                }
            });
        }
        // Mobile: place the scrubber below the stage like the 3D map does, so
        // the stage bottom stays the canvas bottom and the mode badge anchors
        // to the same spot as on 3D instead of dropping onto the scrubber row.
        // Tracked live, not just at mount - the breakpoint CSS applies on
        // resize, so the DOM placement must follow it.
        this._scrubberMq=window.matchMedia('(max-width: 768px)');
        this._placeScrubber=()=>{
            if(this._scrubberMq.matches&&this._el.parentElement){
                this._el.parentElement.insertBefore(scrubber,this._el.nextSibling);
            }else{
                this._el.appendChild(scrubber);
            }
        };
        this._placeScrubber();
        this._scrubberMq.addEventListener('change',this._placeScrubber);
        this._scrubberEl=scrubber;
        this._scrubberEls={
            range:sRange,
            left:scrubber.querySelector('[data-role="left"]'),
            right:scrubber.querySelector('[data-role="right"]'),
            playPause:scrubber.querySelector('[data-role="playpause"]'),
            speedLabel:scrubber.querySelector('[data-role="speed-label"]'),
            windowSel,
            ticks:scrubber.querySelector('[data-role="ticks"]'),
        };
        // Adopt whatever window the 3D map already published (it usually mounts first).
        this._syncScrubberWindow();

        // Events
        canvas.addEventListener('wheel',(e)=>this._onWheel(e),{passive:false});
        canvas.addEventListener('pointerdown',(e)=>this._onDown(e));
        canvas.addEventListener('pointermove',(e)=>this._onMove(e));
        canvas.addEventListener('pointerup',(e)=>this._onUp(e));
        canvas.addEventListener('dblclick',(e)=>this._onDoubleClick(e));
        canvas.addEventListener('pointerleave',(e)=>{this._onUp(e);if(this._pointers.size===0&&e.pointerType==='mouse')this._hideTooltip();});

        // Resize
        this._resizeObs=new ResizeObserver(()=>this._resize());
        this._resizeObs.observe(this._el);
        this._resize();
    }

    _resize(){
        const rect=this._canvas.getBoundingClientRect();
        this._dpr=window.devicePixelRatio||1;
        this._cw=rect.width; this._ch=rect.height;
        this._canvas.width=rect.width*this._dpr;
        this._canvas.height=rect.height*this._dpr;
        this._staticCanvas.width=this._canvas.width;
        this._staticCanvas.height=this._canvas.height;
        this._needsStaticRedraw=true;
        if(this._isFitted)this._fitAll();
    }

    // ---- Pan / Zoom ----

    _screenToWorld(sx,sy){
        return{x:(sx-this._cw/2)/this._scale+this._ox, y:(sy-this._ch/2)/this._scale+this._oy};
    }

    _onWheel(e){
        e.preventDefault();
        const rect=this._canvas.getBoundingClientRect();
        const sx=e.clientX-rect.left, sy=e.clientY-rect.top;
        const wBefore=this._screenToWorld(sx,sy);
        const step=1+Math.min(Math.abs(e.deltaY),100)*0.002;
        const factor=e.deltaY<0?step:1/step;
        this._scale=Math.max(0.05,Math.min(10,this._scale*factor));
        const wAfter=this._screenToWorld(sx,sy);
        this._ox+=wBefore.x-wAfter.x;
        this._oy+=wBefore.y-wAfter.y;
        this._isFitted=false;
        this._needsStaticRedraw=true;
    }

    _onDown(e){
        this._pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});
        this._canvas.setPointerCapture(e.pointerId);
        this._tapStart={x:e.clientX,y:e.clientY,id:e.pointerId};

        if(this._pointers.size===2){
            this._dragging=false;
            this._dragStart=null;
            this._tapStart=null;
            const pts=[...this._pointers.values()];
            this._pinchStartDist=Math.hypot(pts[1].x-pts[0].x,pts[1].y-pts[0].y);
            this._pinchStartScale=this._scale;
            this._pinchStartCenter={x:(pts[0].x+pts[1].x)/2,y:(pts[0].y+pts[1].y)/2};
            this._pinchStartOx=this._ox;
            this._pinchStartOy=this._oy;
            return;
        }

        if(e.button!==0&&e.pointerType==='mouse')return;
        this._dragging=true;
        this._dragStart={x:e.clientX,y:e.clientY,ox:this._ox,oy:this._oy};
        this._canvas.style.cursor='grabbing';
    }

    _onMove(e){
        if(this._pointers.has(e.pointerId))
            this._pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});

        if(this._pointers.size===2&&this._pinchStartDist>0){
            const pts=[...this._pointers.values()];
            const dist=Math.hypot(pts[1].x-pts[0].x,pts[1].y-pts[0].y);
            const center={x:(pts[0].x+pts[1].x)/2,y:(pts[0].y+pts[1].y)/2};

            const newScale=Math.max(0.05,Math.min(10,this._pinchStartScale*(dist/this._pinchStartDist)));

            const rect=this._canvas.getBoundingClientRect();
            const cx0=this._pinchStartCenter.x-rect.left;
            const cy0=this._pinchStartCenter.y-rect.top;
            const wx=(cx0-this._cw/2)/this._pinchStartScale+this._pinchStartOx;
            const wy=(cy0-this._ch/2)/this._pinchStartScale+this._pinchStartOy;

            const cx1=center.x-rect.left;
            const cy1=center.y-rect.top;

            this._scale=newScale;
            this._ox=wx-(cx1-this._cw/2)/newScale;
            this._oy=wy-(cy1-this._ch/2)/newScale;
            this._isFitted=false;
            this._needsStaticRedraw=true;
            return;
        }

        const rect=this._canvas.getBoundingClientRect();
        const sx=e.clientX-rect.left, sy=e.clientY-rect.top;
        if(this._dragging&&this._dragStart){
            if(this._tapStart&&Math.hypot(e.clientX-this._tapStart.x,e.clientY-this._tapStart.y)>=8)
                this._tapStart=null;
            const dx=(e.clientX-this._dragStart.x)/this._scale;
            const dy=(e.clientY-this._dragStart.y)/this._scale;
            this._ox=this._dragStart.ox-dx;
            this._oy=this._dragStart.oy-dy;
            this._isFitted=false;
            this._needsStaticRedraw=true;
        } else {
            this._hitTest(sx,sy);
        }
    }

    _onUp(e){
        const wasTap=this._tapStart&&this._tapStart.id===e.pointerId
            &&Math.hypot(e.clientX-this._tapStart.x,e.clientY-this._tapStart.y)<8;

        this._pointers.delete(e.pointerId);
        if(this._pointers.size<2)this._pinchStartDist=0;

        if(this._pointers.size===1){
            const remaining=[...this._pointers.values()][0];
            this._dragging=true;
            this._dragStart={x:remaining.x,y:remaining.y,ox:this._ox,oy:this._oy};
            this._tapStart=null;
            return;
        }

        if(this._pointers.size===0){
            this._dragging=false;
            this._dragStart=null;
            this._canvas.style.cursor='grab';

            if(wasTap&&e.pointerType!=='mouse'){
                const rect=this._canvas.getBoundingClientRect();
                const sx=e.clientX-rect.left, sy=e.clientY-rect.top;
                this._hitTest(sx,sy);
            }
        }
        this._tapStart=null;
    }

    _fitAll(){
        if(!this._root)return;
        this._calcBounds(true);
        const margin=10;
        // On desktop the scrubber bar overlays the bottom of the stage (on mobile
        // it's moved below the stage, so no overlap - no inset there). Reserve just
        // the bar's height plus a few px so the bottom row sits right above it,
        // without the wasteful gap a full extra margin would leave.
        const overlays=this._scrubberEl&&this._scrubberEl.parentElement===this._el;
        const topPad=margin;
        const bottomPad=overlays?(this._scrubberEl.offsetHeight+4):margin;
        const availH=Math.max(1,this._ch-topPad-bottomPad);
        const sx=(this._cw-margin*2)/this._bw;
        const sy=availH/this._bh;
        this._scale=Math.min(sx,sy,2);
        this._ox=this._bx+this._bw/2;
        // Center within the [top .. just above the bar] band rather than the raw
        // canvas, lifting the content clear of the scrubber bar.
        this._oy=this._by+this._bh/2+(bottomPad-topPad)/(2*this._scale);
        this._isFitted=true;
        this._needsStaticRedraw=true;
    }

    _syncScrubber(){
        if(!this._scrubberEls)return;
        const s=flowData.getScrubber();
        const mode=flowData.getMode();
        const paused=flowData.isPaused();
        this._scrubberEls.range.value=s.value;
        // Live-but-paused shows Live (Paused) on the time label so frozen
        // rates aren't mistaken for live data. Historic keeps its timestamp.
        this._scrubberEls.right.textContent=(mode!=='historic'&&paused)?'Live (Paused)':s.right;
        this._scrubberEls.speedLabel.textContent=`${s.speed}x`;
        this._scrubberEls.playPause.textContent=paused?'▶':'⏸';
        // Mode badge
        if(this._modeBadge){
            this._modeBadge.textContent=mode==='historic'?'Historic':'Live';
            this._modeBadge.classList.toggle('is-historic',mode==='historic');
            this._modeBadge.style.cursor=mode==='historic'?'pointer':'';
            if(mode==='historic'){
                this._modeBadge.setAttribute('data-tooltip','Click to return to live');
            }else{
                this._modeBadge.removeAttribute('data-tooltip');
                if(this._modeBadge._tippy)this._modeBadge._tippy.destroy();
            }
        }
    }

    _syncScrubberWindow(){
        if(!this._scrubberEls)return;
        const win=flowData.getScrubberWindow();
        if(!win)return;
        this._scrubberEls.left.textContent=win.leftLabel;
        const sel=this._scrubberEls.windowSel;
        if(sel){
            sel.value=win.presetKey;
            for(const opt of sel.options)opt.disabled=win.disabledKeys?.includes(opt.value)??false;
        }
        flowData.renderScrubberTicks(this._scrubberEls.ticks,win.startMs,win.endMs);
    }

    _isNodeVisible(n){
        const k=n.d.kind;
        if(k===NK.WifiClient){
            if(!this._overlays.wifiClients)return false;
            if(n.d.band&&!this._filter.bands[n.d.band])return false;
        }
        if(k===NK.WiredClient&&!this._overlays.wiredClients)return false;
        if(this._filter.text){
            const t=this._filter.text;
            const name=(n.d.name||'').toLowerCase();
            const mac=(n.d.mac||'').toLowerCase();
            const ip=(n.d.ip||'').toLowerCase();
            if(!name.includes(t)&&!mac.includes(t)&&!ip.includes(t))return false;
        }
        return true;
    }

    _isCloudVisible(){return this._overlays.clouds;}

    _zoomBy(factor){
        this._scale=Math.max(0.05,Math.min(10,this._scale*factor));
        this._isFitted=false;
        this._needsStaticRedraw=true;
    }

    _toggleFullscreen(){
        this._isFullscreen=!this._isFullscreen;
        const el=this._el;
        if(this._isFullscreen){
            el.classList.add('lan-flow-map-fullscreen');
            this._fsBtn.innerHTML=`<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                <polyline points="4 10 10 10 10 4"></polyline><polyline points="14 4 14 10 20 10"></polyline>
                <polyline points="20 14 14 14 14 20"></polyline><polyline points="10 20 10 14 4 14"></polyline></svg>`;
            this._fsBtn.setAttribute('data-tooltip','Exit fullscreen (Esc)');
        }else{
            el.classList.remove('lan-flow-map-fullscreen');
            this._fsBtn.innerHTML=`<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                <polyline points="3 8 3 3 8 3"></polyline><polyline points="16 3 21 3 21 8"></polyline>
                <polyline points="21 16 21 21 16 21"></polyline><polyline points="8 21 3 21 3 16"></polyline></svg>`;
            this._fsBtn.setAttribute('data-tooltip','Fullscreen');
        }
        requestAnimationFrame(()=>requestAnimationFrame(()=>{this._resize();this._fitAll();}));
    }

    // ---- Tooltip hit-test ----

    _nodeAt(sx,sy){
        const w=this._screenToWorld(sx,sy);
        let hit=null;

        const checkNode=(n)=>{
            if(n.d.kind===NK.VirtualHub){
                if(Math.abs(w.x-n.x)<20&&Math.abs(w.y-n.y)<20)hit=n;
                return;
            }
            if(Math.abs(w.x-n.x)<G.boxW/2&&Math.abs(w.y-n.y)<G.boxH/2)hit=n;
            for(const c of n.infra)checkNode(c);
            for(const c of n.clients.slice(0,G.maxClients)){
                if(this._isNodeVisible(c)&&Math.abs(w.x-c.x)<G.clientCellW/2&&Math.abs(w.y-c.y)<G.clientCellH/2)hit=c;
            }
        };
        if(this._root)checkNode(this._root);
        return hit;
    }

    // Double-click a client to open its performance dashboard (matches the 3D map).
    _onDoubleClick(e){
        const rect=this._canvas.getBoundingClientRect();
        const hit=this._nodeAt(e.clientX-rect.left,e.clientY-rect.top);
        if(!hit)return;
        const d=hit.d;
        // Switches and gateways scroll to the port stats table and isolate that device.
        if(d.kind===NK.Switch||d.kind===NK.Gateway){
            if(d.mac&&window.__portStatsTable){
                window.__portStatsTable.selectDevice(d.mac);
                document.getElementById('port-stats-card')?.scrollIntoView({behavior:'smooth',block:'start'});
            }
            return;
        }
        if(d.kind!==NK.WifiClient&&d.kind!==NK.WiredClient)return;
        if(!d.ip)return;
        // Wi-Fi clients land on the Signal tab; wired clients go to the default tab.
        const tab=d.kind===NK.WifiClient?'&tab=signal':'';
        const url=`/client-dashboard?ip=${encodeURIComponent(d.ip)}${tab}`;
        window.location.href=window.noSiteContext?window.noSiteContext.stampUrl(url):url;
    }

    _hitTest(sx,sy){
        const hit=this._nodeAt(sx,sy);

        if(hit&&hit!==this._hoverNode){
            this._hoverNode=hit;
            this._showTooltip(hit,sx,sy);
        } else if(hit&&hit===this._hoverNode){
            const tr=this._tooltip.getBoundingClientRect();
            const cr=this._el.getBoundingClientRect();
            let tx=sx+14,ty=sy+14;
            if(tx+tr.width>cr.width-4)tx=sx-tr.width-8;
            if(ty+tr.height>cr.height-4)ty=sy-tr.height-8;
            if(tx<4)tx=4; if(ty<4)ty=4;
            this._tooltip.style.left=tx+'px';
            this._tooltip.style.top=ty+'px';
        } else if(!hit){
            this._hideTooltip();
        }
    }

    _showTooltip(node,sx,sy){
        const d=node.d;
        const esc=(s)=>(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;');
        const m=demoMask;
        // During playback prefer the client's wireless stats at the scrubbed instant.
        const cs=flowData.getClientStats()?.[d.id];
        const band=cs?.band??d.band;
        const signalDbm=cs?.signalDbm??d.signalDbm;
        const rows=[];
        if(d.ip)rows.push(['IP',m(d.ip)]);
        if(d.mac)rows.push(['MAC',m(d.mac)]);
        if(d.model)rows.push(['Model',d.model]);
        if(band)rows.push(['Band',`${band} GHz`]);
        if(d.ssid)rows.push(['SSID',m(d.ssid)]);
        if(d.network)rows.push(['Network',m(d.network)]);
        if(signalDbm)rows.push(['Signal',`${signalDbm} dBm`]);
        if(d.switchPortName)rows.push(['Switch port',m(d.switchPortName)]);

        const badges=flowData.getNodeBadges();
        const b=badges?.[d.id];
        if(b?.cpuPercent!=null)rows.push(['CPU',`${b.cpuPercent.toFixed(0)}%`]);
        if(b?.memoryUsedPercent!=null)rows.push(['Memory',`${b.memoryUsedPercent.toFixed(0)}%`]);
        if(b?.temperatureC!=null)rows.push(['Temp',`${b.temperatureC.toFixed(0)} °C`]);
        if(b?.uptimeSeconds!=null){
            const days=Math.floor(b.uptimeSeconds/86400);
            const hrs=Math.floor((b.uptimeSeconds%86400)/3600);
            rows.push(['Uptime',days>0?`${days}d ${hrs}h`:`${hrs}h`]);
        }

        // Rates: fabric devices use badge, clients use link rates
        const isFab=d.kind===NK.Gateway||d.kind===NK.Switch||d.kind===NK.AP;
        let inBps=0,outBps=0,any=false;
        if(d.kind===NK.VirtualHub){
            // Hub throughput is its upstream port link, not the summed members.
            const hr=this._virtualHubRates(d.id);inBps=hr.down;outBps=hr.up;any=hr.any;
        }else if(isFab&&b){
            if(b.fabricIngressBps!=null||b.fabricEgressBps!=null){
                inBps=b.fabricIngressBps||0;outBps=b.fabricEgressBps||0;any=true;
            }else if(b.aggregateInBps!=null||b.aggregateOutBps!=null){
                inBps=b.aggregateInBps||0;outBps=b.aggregateOutBps||0;any=true;
            }
        }else{
            for(const e of this._edges){
                if(e.lk.fromNodeId!==d.id&&e.lk.toNodeId!==d.id)continue;
                const r=this._liveRates[e.lk.portKey]||this._liveRates[e.lk.id];
                if(!r)continue;
                any=true;
                const dl=r.downstreamBps||0,ul=r.upstreamBps||0;
                if(e.lk.toNodeId===d.id){inBps+=dl;outBps+=ul;}
                else{inBps+=ul;outBps+=dl;}
            }
        }
        // A device on a wireless mesh uplink (mesh AP, or a UDB - UniFi Device Bridge -
        // which classifies as a Switch) has PHY/throughput polarity reverse of a Wi-Fi
        // client's. Detect via the device's OWN MeshBackhaul uplink (toNodeId), kind agnostic.
        let isMeshUplink=false;
        for(const e of this._edges){if(e.lk.toNodeId===d.id&&e.lk.kind===LK.MeshBackhaul){isMeshUplink=true;break;}}

        // Negotiated link speed (wired port or wireless PHY), directly above throughput.
        if(d.wiredLinkSpeedMbps)rows.push(['Link speed',formatSpeed(d.wiredLinkSpeedMbps)]);
        else if(d.phyTxKbps||d.phyRxKbps){
            // Device perspective: download (↓) is the AP's TX to a Wi-Fi client, upload
            // (↑) is the AP's RX. A mesh uplink's Tx/Rx is the reverse, so swap.
            const downKbps=isMeshUplink?d.phyRxKbps:d.phyTxKbps;
            const upKbps=isMeshUplink?d.phyTxKbps:d.phyRxKbps;
            const dl=downKbps?`↓${formatSpeed(Math.round(downKbps/1000))}`:'';
            const ul=upKbps?`↑${formatSpeed(Math.round(upKbps/1000))}`:'';
            rows.push(['Link speed',`${dl}${dl&&ul?'  ':''}${ul}`]);
        }
        if(any){
            // APs: uplink throughput flipped to the to-gateway (fabric) direction.
            // Wired-backhaul APs get the 'Wired' qualifier; mesh-uplink APs don't.
            if(d.kind===NK.AP){
                rows.push([isMeshUplink?'Ingress':'Wired ingress',formatBps(outBps)]);
                rows.push([isMeshUplink?'Egress':'Wired egress',formatBps(inBps)]);
            }
            else if(isFab){rows.push(['Ingress',formatBps(inBps)]);rows.push(['Egress',formatBps(outBps)]);}
            else{rows.push(['Download',formatBps(inBps)]);rows.push(['Upload',formatBps(outBps)]);}
        }

        this._tooltip.innerHTML=
            `<div style="font-weight:600;margin-bottom:3px">${esc(m(d.name||d.mac||''))}</div>`
            +rows.map(([k,v])=>`<div style="display:flex;justify-content:space-between;gap:12px"><span style="color:${C.textMuted}">${k}</span><span>${esc(String(v))}</span></div>`).join('');
        this._tooltip.style.opacity='1';
        this._tooltip.style.visibility='visible';
        // Position dynamically to stay within the container
        const tr=this._tooltip.getBoundingClientRect();
        const cr=this._el.getBoundingClientRect();
        let tx=sx+14, ty=sy+14;
        if(tx+tr.width>cr.width-4)tx=sx-tr.width-8;
        if(ty+tr.height>cr.height-4)ty=sy-tr.height-8;
        if(tx<4)tx=4;
        if(ty<4)ty=4;
        this._tooltip.style.left=tx+'px';
        this._tooltip.style.top=ty+'px';
    }

    _hideTooltip(){
        this._hoverNode=null;
        if(this._tooltip){this._tooltip.style.opacity='0';this._tooltip.style.visibility='hidden';}
    }

    // ---- Contour-based layout (Reingold-Tilford style) ----
    // Computes left/right extent at every depth for each subtree, then pushes
    // siblings apart until no depth overlaps. Guarantees zero cross-tree overlap.

    _buildLayout(snap){
        this._snapshot=snap;
        // Tree is rebuilt from the snapshot's (current) associations, so any historic
        // roam re-parenting is gone - reset the applied map so _applyClientAssoc re-diffs.
        this._appliedAssoc=new Map();
        const byId=new Map();
        for(const n of snap.nodes)byId.set(n.id,new TN(n));
        this._treeMap=byId;

        const adj=new Map();
        for(const lk of snap.links){
            if(lk.kind===LK.Wan||lk.kind===LK.Transit)continue;
            const a=lk.fromNodeId,b=lk.toNodeId;
            if(!byId.has(a)||!byId.has(b))continue;
            if(!adj.has(a))adj.set(a,[]);
            if(!adj.has(b))adj.set(b,[]);
            adj.get(a).push({to:b,lk});
            adj.get(b).push({to:a,lk});
        }

        let root=null;
        for(const[,tn]of byId){if(tn.d.kind===NK.Gateway){root=tn;break;}}
        this._root=root;

        if(root){
            const visited=new Set([root.d.id]);
            const queue=[root];
            while(queue.length>0){
                const par=queue.shift();
                for(const{to}of(adj.get(par.d.id)||[])){
                    if(visited.has(to))continue;
                    visited.add(to);
                    const child=byId.get(to);
                    if(!child)continue;
                    if(isClient(child.d.kind))par.clients.push(child);
                    else{par.infra.push(child);queue.push(child);}
                }
            }
        }

        this._clouds=(snap.clouds||[]).map(c=>({d:c,x:0,y:0}));
        this._edges=[];
        for(const lk of snap.links)this._edges.push({lk,fn:byId.get(lk.fromNodeId),tn:byId.get(lk.toNodeId)});

        if(!root)return;
        this._spreadFactor=this._getSpreadFactor();
        this._contourLayout(root);
        this._assignAbsoluteXY(root,0,0);
        this._placeClouds();
        this._matchEdges();
        // Always reinit streams - edges get new endpoint coords on rebuild
        this._initStreams();
        this._calcBounds();
        this._updateStreamRates();
    }

    _getSpreadFactor(){
        const wOn=this._overlays.wifiClients;
        const wdOn=this._overlays.wiredClients;
        if(wOn&&wdOn)return 1;
        if(wOn||wdOn)return 1.15;
        return 1.3;
    }

    _relayout(){
        if(!this._root)return;
        this._spreadFactor=this._getSpreadFactor();
        this._contourLayout(this._root);
        this._assignAbsoluteXY(this._root,0,0);
        this._placeClouds();
        this._matchEdges();
        this._initStreams();
        this._calcBounds();
        this._updateStreamRates();
        this._needsStaticRedraw=true;
    }

    // Re-attach wifi clients to the AP they were on at the scrubbed instant (roam
    // playback). The historic update carries clientStats[clientId].apNodeId; in live
    // mode it's empty, so every client falls back to its snapshot parent (reset).
    // Only relayouts when an association actually changed, so steady live is free.
    _applyClientAssoc(){
        if(!this._root)return;
        const stats=flowData.getClientStats()||{};
        if(!this._appliedAssoc)this._appliedAssoc=new Map();
        const now=performance.now();
        let changed=false;
        for(const[id,tn]of this._treeMap){
            if(!isClient(tn.d.kind))continue;
            const baseline=tn.d.parentId;
            const desired=stats[id]?.apNodeId||baseline;
            const cur=this._appliedAssoc.get(id)||baseline;
            if(desired===cur)continue;
            const newParent=this._treeMap.get(desired);
            if(!newParent)continue; // historic AP not in current topology - leave in place
            const curParent=this._treeMap.get(cur);
            if(curParent){const i=curParent.clients.indexOf(tn);if(i>=0)curParent.clients.splice(i,1);}
            newParent.clients.push(tn);
            const edge=this._edges.find(e=>e.tn===tn||e.fn===tn);
            if(edge){if(edge.tn===tn)edge.fn=newParent;else edge.tn=newParent;}
            this._appliedAssoc.set(id,desired);
            tn._roamFadeUntil=now+ROAM_FADE_MS;
            changed=true;
        }
        if(changed){this._roamFadeUntil=now+ROAM_FADE_MS;this._relayout();}
    }

    // Compute contour (left/right extent at each depth relative to node x=0)
    // and store relative child offsets on the node.
    _contourLayout(n){
        const selfW=isClient(n.d.kind)?G.clientCellW:G.boxW+40;
        n._isGrid=false;
        const visCl=n.clients.filter(c=>this._isNodeVisible(c));
        const nc=Math.min(visCl.length,G.maxClients);
        n._visClients=visCl.slice(0,G.maxClients);

        // VirtualHub: treat as a leaf (children won't be rendered)
        if(n.d.kind===NK.VirtualHub){
            const hubW=G.clientCellW;
            n._contour=[{l:-hubW/2,r:hubW/2}];
            n._kidOffsets=[];
            n._kids=[];
            return;
        }

        // Pure-client nodes (APs with only WiFi clients): compact grid
        if(n.infra.length===0&&nc>0){
            const cols=Math.min(nc,G.clientCols);
            const rows=Math.ceil(nc/cols);
            const gridW=cols*G.clientCellW;
            const staggerExtra=rows>1?G.clientCellW/2:0;
            n._isGrid=true;
            n._gridCols=cols;
            // Contour: node at depth 0, grid rectangle at depth 1 (widened for stagger)
            n._contour=[
                {l:-selfW/2,r:selfW/2},
                {l:-gridW/2,r:gridW/2+staggerExtra},
            ];
            n._kidOffsets=[];
            n._kids=[];
            return;
        }

        // Infra children; clients always use a grid (placeholder in the kids array)
        const kids=[...n.infra];
        if(nc>0){
            const cols=Math.min(nc,G.clientCols);
            const rows=Math.ceil(nc/cols);
            const gridW=cols*G.clientCellW;
            const staggerExtra=rows>1?G.clientCellW/2:0;
            n._isGrid=true;
            n._gridCols=cols;
            const gp={_isGridPlaceholder:true,_contour:[{l:-gridW/2,r:gridW/2+staggerExtra}]};
            kids.push(gp);
        }

        if(kids.length===0){
            n._contour=[{l:-selfW/2,r:selfW/2}];
            n._kidOffsets=[];
            n._kids=[];
            return;
        }

        for(const k of kids)if(!k._isGridPlaceholder)this._contourLayout(k);

        const GAP=20;
        const offsets=[];
        let groupRight=[];

        for(let i=0;i<kids.length;i++){
            const cc=kids[i]._contour;
            if(i===0){
                offsets[0]=0;
                groupRight=cc.map(c=>c.r);
            }else{
                let minOff=0;
                for(let d=0;d<Math.min(groupRight.length,cc.length);d++){
                    const needed=groupRight[d]-cc[d].l+GAP;
                    if(needed>minOff)minOff=needed;
                }
                offsets[i]=minOff;
                for(let d=0;d<cc.length;d++){
                    const sr=cc[d].r+minOff;
                    if(d<groupRight.length){if(sr>groupRight[d])groupRight[d]=sr;}
                    else groupRight.push(sr);
                }
            }
        }

        const firstL=offsets[0]+kids[0]._contour[0].l;
        const lastR=offsets[offsets.length-1]+kids[kids.length-1]._contour[0].r;
        const center=(firstL+lastR)/2;
        const centered=offsets.map(o=>o-center);

        n._kidOffsets=centered;
        n._kids=kids;

        const contour=[{l:-selfW/2,r:selfW/2}];
        let maxD=0;
        for(const k of kids)if(k._contour.length>maxD)maxD=k._contour.length;
        for(let d=0;d<maxD;d++){
            let l=Infinity,r=-Infinity;
            for(let i=0;i<kids.length;i++){
                const cc=kids[i]._contour;
                if(d<cc.length){
                    const sl=cc[d].l+centered[i];
                    const sr=cc[d].r+centered[i];
                    if(sl<l)l=sl;
                    if(sr>r)r=sr;
                }
            }
            if(l!==Infinity)contour.push({l,r});
        }
        n._contour=contour;
    }

    _assignAbsoluteXY(n,absX,depth){
        const yOff=G.pad+80;
        n.x=absX;
        n.y=yOff+depth*G.tierGap;

        // Client grid: compact rows with honeycomb stagger
        if(n._isGrid&&(!n._kids||n._kids.length===0)){
            const vc=n._visClients||[];
            const nc=vc.length;
            const cols=n._gridCols;
            const gridW=cols*G.clientCellW;
            const gridLeft=absX-gridW/2;
            const clientY=n.y+G.clientTierGap;
            const stagger=G.clientCellW/2;
            for(let i=0;i<nc;i++){
                const col=i%cols,row=Math.floor(i/cols);
                const rowOff=(row%2)*stagger;
                vc[i].x=gridLeft+col*G.clientCellW+G.clientCellW/2+rowOff;
                vc[i].y=clientY+row*G.clientCellH;
            }
            return;
        }

        const kids=n._kids||[];
        const offsets=n._kidOffsets||[];
        const sf=this._spreadFactor||1;
        for(let i=0;i<kids.length;i++){
            if(kids[i]._isGridPlaceholder){
                const vc=n._visClients||[];
                const nc=vc.length;
                const cols=n._gridCols;
                const gridW=cols*G.clientCellW;
                const px=absX+offsets[i]*sf;
                const gridLeft=px-gridW/2;
                const clientY=n.y+G.clientTierGap;
                const stagger=G.clientCellW/2;
                for(let j=0;j<nc;j++){
                    const col=j%cols,row=Math.floor(j/cols);
                    const rowOff=(row%2)*stagger;
                    vc[j].x=gridLeft+col*G.clientCellW+G.clientCellW/2+rowOff;
                    vc[j].y=clientY+row*G.clientCellH;
                }
            }else{
                this._assignAbsoluteXY(kids[i],absX+offsets[i]*sf,depth+1);
            }
        }
    }

    _updateSnapshotData(snap){
        // Update cloud data (ISP speeds, RTT) without rebuilding layout
        for(const c of this._clouds){
            const fresh=snap.clouds?.find(sc=>sc.id===c.d.id);
            if(fresh){
                c.d.ispDownloadMbps=fresh.ispDownloadMbps;
                c.d.ispUploadMbps=fresh.ispUploadMbps;
                c.d.rttAvgMs=fresh.rttAvgMs;
                c.d.lossPercent=fresh.lossPercent;
            }
        }
        // Update node data and detect client changes per parent
        const newNodeMap=new Map();
        for(const n of snap.nodes)newNodeMap.set(n.id,n);

        let clientsChanged=false;
        for(const n of snap.nodes){
            const tn=this._treeMap.get(n.id);
            if(tn){
                // A client that roamed to a different AP (live) keeps its node id, so the
                // only signal is its parent changing. Rebuild so it moves to the new AP.
                if(isClient(n.kind)&&tn.d.parentId!==n.parentId)clientsChanged=true;
                tn.d.online=n.online;
                tn.d.phyTxKbps=n.phyTxKbps;
                tn.d.phyRxKbps=n.phyRxKbps;
                tn.d.band=n.band;
                tn.d.signalDbm=n.signalDbm;
            }else if(isClient(n.kind)){
                clientsChanged=true;
            }
        }
        // Check for removed clients
        for(const[id,tn]of this._treeMap){
            if(isClient(tn.d.kind)&&!newNodeMap.has(id))clientsChanged=true;
        }
        // If clients changed, do a targeted layout rebuild
        if(clientsChanged){
            this._liveRates={...flowData.getLiveRates()};
            this._buildLayout(snap);
        }
    }

    _placeClouds(){
        if(!this._root||this._clouds.length===0)return;
        const gx=this._root.x,gy=this._root.y;
        const total=this._clouds.length,sp=G.cloudGap;
        const sx=gx-((total-1)*sp)/2;
        const baseY=gy-G.tierGap*1.6;
        const stagger=45;
        for(let i=0;i<total;i++){
            this._clouds[i].x=sx+i*sp;
            this._clouds[i].y=baseY+(1-i%2)*stagger;
        }
    }

    _matchEdges(){
        const gw=this._root;
        if(!gw)return;
        const gwT=gw.y-G.boxH/2;
        const STAGGER=6;

        // WAN cloud links - stagger by index
        const wanEdges=[];
        for(const cloud of this._clouds){
            const cy=cloud.y+G.cloudR+1;
            const edge=this._edges.find(e=>
                (e.lk.kind===LK.Wan||e.lk.kind===LK.Transit)
                &&(e.lk.fromNodeId===cloud.d.id||e.lk.toNodeId===cloud.d.id));
            if(edge){edge._x1=cloud.x;edge._y1=cy;edge._x2=gw.x;edge._y2=gwT;edge._isWan=true;wanEdges.push(edge);}
        }
        const wanMid=(wanEdges.length-1)/2;
        for(let i=0;i<wanEdges.length;i++)wanEdges[i]._midYOff=(i-wanMid)*STAGGER;

        // Tree links - stagger siblings from same parent
        const matchTree=(n)=>{
            // VirtualHub: only match the hub's own uplink, skip its children
            if(n.d.kind===NK.VirtualHub)return;

            const pB=n.y+G.boxH/2;
            const sibEdges=[];

            for(const c of n.infra){
                // VirtualHub renders as a small ring (r=10), not a full box
                const cT=c.d.kind===NK.VirtualHub?c.y-12:c.y-G.boxH/2;
                const edge=this._edges.find(e=>
                    (e.lk.fromNodeId===n.d.id&&e.lk.toNodeId===c.d.id)
                    ||(e.lk.fromNodeId===c.d.id&&e.lk.toNodeId===n.d.id));
                if(edge){edge._x1=n.x;edge._y1=pB;edge._x2=c.x;edge._y2=cT;edge._isCl=false;sibEdges.push(edge);}
                matchTree(c);
            }
            for(const c of n.clients.slice(0,G.maxClients)){
                const cT=c.y-G.clientR;
                // Match the client's uplink edge by TN identity, not link ids: during
                // roam playback the client is re-parented to its historic AP, so the
                // edge must follow the tree without mutating the shared snapshot link.
                // A client has exactly one tree edge (its uplink), so this is unambiguous.
                const edge=this._edges.find(e=>e.tn===c||e.fn===c);
                if(edge){edge._x1=n.x;edge._y1=pB;edge._x2=c.x;edge._y2=cT;edge._isCl=true;edge._band=edge.lk.band;sibEdges.push(edge);}
            }

            // Stagger horizontal segments of siblings that share the same parent
            if(sibEdges.length>1){
                const mid=(sibEdges.length-1)/2;
                for(let i=0;i<sibEdges.length;i++)sibEdges[i]._midYOff=(i-mid)*STAGGER;
                // Multi-row grids: push client offshoots down so the trunk
                // drops further before branching horizontally
                const nCl=sibEdges.filter(e=>e._isCl).length;
                if(nCl>G.clientCols){
                    for(const e of sibEdges)if(e._isCl)e._midYOff+=12;
                }
            }
        };
        matchTree(gw);
    }

    _initStreams(){
        this._streams=[];
        for(const e of this._edges){
            if(e._x1==null)continue;
            const len=orthoLen(e._x1,e._y1,e._x2,e._y2,e._midYOff);
            if(len<5)continue;
            e._sDown=new Stream(e,1,C.downstream);
            e._sUp=new Stream(e,-1,C.upstream);
            this._streams.push(e._sDown,e._sUp);
        }
    }

    _calcBounds(visibleOnly){
        let x0=Infinity,y0=Infinity,x1=-Infinity,y1=-Infinity;
        // Vertical extent is asymmetric on purpose: name + throughput labels sit BELOW a
        // node and there's nothing above the top node, so reserve only the node's top
        // half upward and the label band downward. (The old code used boxW - the box
        // WIDTH - for all four sides, padding empty space above the gateway and clipping
        // the bottom node's rate label, which biased the whole map downward.)
        const exp=(x,y,rx,up,down)=>{
            x0=Math.min(x0,x-rx); x1=Math.max(x1,x+rx);
            y0=Math.min(y0,y-up); y1=Math.max(y1,y+down);
        };
        const INFRA_UP=G.boxH/2+6, INFRA_DOWN=G.boxH/2+44;   // box top ; box + name + rate label
        const CLIENT_UP=G.clientR+4, CLIENT_DOWN=G.clientR+24; // dot top ; dot + name label
        const walk=(n)=>{
            exp(n.x,n.y,G.boxW,INFRA_UP,INFRA_DOWN);
            for(const c of n.infra)walk(c);
            for(const c of n.clients.slice(0,G.maxClients)){
                if(visibleOnly&&!this._isNodeVisible(c))continue;
                exp(c.x,c.y,G.clientCellW/2,CLIENT_UP,CLIENT_DOWN);
            }
        };
        walk(this._root);
        if(!visibleOnly||this._isCloudVisible()){
            for(const c of this._clouds)exp(c.x,c.y,G.cloudR+30,G.cloudR+30,G.cloudR+30);
        }
        const p=20;
        this._bx=x0-p; this._by=y0-p;
        this._bw=(x1-x0)+p*2; this._bh=(y1-y0)+p*2;
    }

    // ---- Image preloading ----

    async _loadImages(snap){
        const models=new Set();
        for(const n of snap.nodes){
            if(n.model&&isInfra(n.kind))models.add(n.model.toLowerCase().replace(/ /g,'-'));
        }
        const promises=[];
        for(const m of models){
            if(this._images.has(m))continue;
            promises.push(new Promise(resolve=>{
                const img=new Image();
                img.onload=()=>{this._images.set(m,img);resolve();};
                img.onerror=()=>resolve();
                img.src=`/images/devices/${m}.png`;
            }));
        }
        if(promises.length>0)await Promise.all(promises);
    }

    // ---- Rate updates ----

    // A device with an offline badge can't be moving traffic. Badges only carry
    // online for infra (gateway/switch/AP); a node with no badge is assumed online so
    // clients (which have no badge) are never spuriously zeroed.
    _isOffline(nodeId){
        const b=flowData.getNodeBadges()?.[nodeId];
        return b?b.online===false:false;
    }

    _updateStreamRates(){
        for(const e of this._edges){
            if(!e._sDown)continue;
            // Force idle when an endpoint is offline - _liveRates is merged, not
            // replaced, so the last sample would otherwise stream forever.
            const off=this._isOffline(e.lk.fromNodeId)||this._isOffline(e.lk.toNodeId);
            const r=off?null:(this._liveRates[e.lk.portKey]||this._liveRates[e.lk.id]);
            e._sDown.setRate(r?.downstreamBps??0);
            e._sUp.setRate(r?.upstreamBps??0);
        }
    }

    _updateCloudStats(){
        const cs=flowData.getCloudStats();
        for(const cloud of this._clouds){
            const live=cs?.[cloud.d.id];
            if(live){
                if(live.rttAvgMs!=null)cloud.d.rttAvgMs=live.rttAvgMs;
                if(live.lossPercent!=null)cloud.d.lossPercent=live.lossPercent;
            }
        }
    }

    // ---- Draw (called every frame) ----

    _draw(){
        const canvas=this._canvas, ctx=this._ctx;
        const dpr=this._dpr, cw=this._cw, ch=this._ch;
        if(!canvas||cw===0)return;

        // Redraw static layers to offscreen canvas when transform changed
        if(this._needsStaticRedraw){
            this._needsStaticRedraw=false;
            this._drawStatic();
        }

        // Composite: static + particles
        ctx.setTransform(1,0,0,1,0,0);
        ctx.drawImage(this._staticCanvas,0,0);

        // Draw particles on top
        ctx.setTransform(this._scale*dpr,0,0,this._scale*dpr,
            (cw/2-this._ox*this._scale)*dpr,
            (ch/2-this._oy*this._scale)*dpr);

        ctx.globalCompositeOperation='lighter';
        for(const s of this._streams){
            if(s.edge._isWan&&!this._isCloudVisible())continue;
            if(s.edge._isCl){const child=s.edge.tn||s.edge.fn;if(child&&!this._isNodeVisible(child))continue;}
            ctx.fillStyle=s.color;
            for(const sl of s.slots){
                if(sl.t<0)continue;
                const pt=orthoAt(s.edge._x1,s.edge._y1,s.edge._x2,s.edge._y2,sl.t,s.midYOff);
                ctx.globalAlpha=0.85;
                ctx.beginPath();
                ctx.arc(pt.x,pt.y,sl.size,0,Math.PI*2);
                ctx.fill();
            }
        }
        ctx.globalCompositeOperation='source-over';
        ctx.globalAlpha=1;

        // Labels on top of everything (including particles)
        this._drawLinkSpeedLabels(ctx);
        this._drawRateLabels(ctx);
    }

    _drawStatic(){
        const c=this._staticCanvas;
        const ctx=c.getContext('2d');
        const dpr=this._dpr, cw=this._cw, ch=this._ch;

        ctx.setTransform(1,0,0,1,0,0);
        ctx.fillStyle=C.bg;
        ctx.fillRect(0,0,c.width,c.height);

        // Apply pan/zoom transform
        ctx.setTransform(this._scale*dpr,0,0,this._scale*dpr,
            (cw/2-this._ox*this._scale)*dpr,
            (ch/2-this._oy*this._scale)*dpr);

        if(!this._root)return;

        // Links (pipes only)
        this._pendingLinkLabels=[];
        this._drawAllLinks(ctx);
        // Clouds (if overlay enabled)
        if(this._isCloudVisible())this._drawAllClouds(ctx);
        // Infra + client nodes
        this._drawAllNodes(ctx,this._root);
    }

    // ---- Link drawing ----

    _drawAllLinks(ctx){
        for(const e of this._edges){
            if(e._x1==null)continue;
            // Hide links to filtered-out nodes
            if(e._isWan&&!this._isCloudVisible())continue;
            if(e._isCl){
                const child=e.tn||e.fn;
                if(child&&!this._isNodeVisible(child))continue;
            }
            const off=this._isOffline(e.lk.fromNodeId)||this._isOffline(e.lk.toNodeId);
            const r=off?null:(this._liveRates[e.lk.portKey]||this._liveRates[e.lk.id]);
            const dn=r?.downstreamBps??0,up=r?.upstreamBps??0;
            const cap=e.lk.capacityBps||1e9;
            // Full-duplex: reserve the top (red) colour for BOTH directions
            // saturated. Busy direction drives the ramp; the quiet one must also
            // load up to reach full - a lone saturated direction tops out amber.
            const dU=Math.min(dn/cap,1),uU=Math.min(up/cap,1);
            const u=0.75*Math.max(dU,uU)+0.25*Math.min(dU,uU);
            const band=e.lk.band;
            ctx.strokeStyle=pipeClr(Math.min(u,1),band);
            ctx.lineWidth=pipeW(e.lk.capacityBps);
            ctx.lineCap='round';
            ctx.globalAlpha=e._isCl?0.3+u*0.45:0.5+u*0.5;
            strokeOrtho(ctx,e._x1,e._y1,e._x2,e._y2,e._midYOff);

            // Capacity / speed label on infra and WAN links
            if(!e._isCl){
                ctx.globalAlpha=1;
                const isWan=e._isWan;
                const midY=(e._y1+e._y2)/2+(e._midYOff||0);
                // WAN: on cloud's vertical above horizontal. Infra: on child's vertical below horizontal.
                const mx=isWan?e._x1:e._x2;
                const my=isWan?midY-28:midY+20;
                let txt=null;
                let txtColor=C.textMuted; // default muted; live rates override
                let txtItalic=false;

                if(isWan){
                    // Show live throughput when active, ISP expected when idle
                    const lr=this._liveRates[e.lk.portKey]||this._liveRates[e.lk.id];
                    const ldn=lr?.downstreamBps??0, lup=lr?.upstreamBps??0;
                    if(ldn>RATE_THRESH||lup>RATE_THRESH){
                        txt='↓'+(ldn>0?formatBps(ldn):'0 bps')+'  ↑'+(lup>0?formatBps(lup):'0 bps');
                        txtColor=null; // use down/up colors
                    }else{
                        const cloud=this._clouds.find(c=>
                            e.lk.fromNodeId===c.d.id||e.lk.toNodeId===c.d.id);
                        if(cloud?.d.ispDownloadMbps&&cloud?.d.ispUploadMbps){
                            txt=`↓${formatSpeed(cloud.d.ispDownloadMbps)}  ↑${formatSpeed(cloud.d.ispUploadMbps)}`;
                            txtColor='#6b7280'; txtItalic=true;
                        }else if(e.lk.capacityBps>0){
                            txt=formatSpeed(e.lk.capacityBps/1e6);
                        }
                    }
                }else if(e.lk.kind===LK.MeshBackhaul||e.lk.band){
                    // Mesh/wireless: show asymmetric PHY rates. During playback prefer the
                    // client's PHY rate at the scrubbed instant over the frozen snapshot.
                    const n1=e.fn?.d, n2=e.tn?.d;
                    const phy=n2?.phyTxKbps?n2:n1?.phyTxKbps?n1:null;
                    const cs=phy?flowData.getClientStats()?.[phy.id]:null;
                    const phyTx=cs?.phyTxKbps??phy?.phyTxKbps, phyRx=cs?.phyRxKbps??phy?.phyRxKbps;
                    if(phyTx&&phyRx){
                        txt=`↓${formatSpeed(phyRx/1000)}  ↑${formatSpeed(phyTx/1000)}`;
                    }else if(e.lk.capacityBps>0){
                        txt=formatSpeed(e.lk.capacityBps/1e6);
                    }
                }else if(e.lk.capacityBps>0){
                    txt=formatSpeed(e.lk.capacityBps/1e6);
                }

                // Suppress the speed/throughput label for a link to an offline device -
                // a down port has no meaningful negotiated speed or throughput.
                if(txt&&!off) this._pendingLinkLabels.push({mx,my,txt,txtColor,txtItalic});
            }
        }
        ctx.globalAlpha=1;
    }

    _drawLinkSpeedLabels(ctx){
        const normalFont=`${G.rateFont}px ${FONT}`;
        const italicFont=`italic ${G.rateFont}px ${FONT}`;
        ctx.font=normalFont;
        for(const{mx,my,txt,txtColor,txtItalic}of(this._pendingLinkLabels||[])){
            ctx.font=txtItalic?italicFont:normalFont;
            const tw=ctx.measureText(txt).width+12;
            ctx.fillStyle=C.labelBg;
            ctx.globalAlpha=1;
            this._roundRect(ctx,mx-tw/2,my-8,tw,16,4);
            ctx.fill();
            if(txtColor){
                ctx.fillStyle=txtColor;
                ctx.textAlign='center'; ctx.textBaseline='middle';
                ctx.fillText(txt,mx,my);
            }else{
                const parts=txt.split(' ↑');
                ctx.textBaseline='middle';
                ctx.textAlign='right'; ctx.fillStyle=C.downstream;
                ctx.fillText(parts[0],mx-4,my);
                ctx.textAlign='left'; ctx.fillStyle=C.upstream;
                ctx.fillText('↑'+parts[1],mx+4,my);
            }
        }
    }

    // ---- Cloud drawing ----

    _drawAllClouds(ctx){
        for(const cloud of this._clouds){
            const cx=cloud.x, cy=cloud.y, r=G.cloudR;
            // Unresolved tier: inactive WAN or discovery pending - render the globe
            // greyed, matching the 3D map.
            const greyed=cloud.d.tier===CT.Unresolved;

            // Subtle radial fill
            const grad=ctx.createRadialGradient(cx-r*0.3,cy-r*0.3,0,cx,cy,r);
            grad.addColorStop(0,greyed?'rgba(120,130,145,0.06)':'rgba(59,130,246,0.08)');
            grad.addColorStop(1,greyed?'rgba(120,130,145,0.02)':'rgba(59,130,246,0.02)');
            ctx.fillStyle=grad;
            ctx.beginPath(); ctx.arc(cx,cy,r,0,Math.PI*2); ctx.fill();

            // Outer circle
            ctx.strokeStyle=greyed?'#3a4455':C.globeStroke;
            ctx.lineWidth=1.5; ctx.globalAlpha=0.8;
            ctx.beginPath(); ctx.arc(cx,cy,r,0,Math.PI*2); ctx.stroke();

            // Longitude lines (3 meridians at different tilts)
            ctx.lineWidth=0.8; ctx.globalAlpha=0.3;
            for(const rx of [0.15, 0.5, 0.85]){
                ctx.beginPath(); ctx.ellipse(cx,cy,r*rx,r,0,0,Math.PI*2); ctx.stroke();
            }

            // Latitude lines (equator + two parallels)
            for(const ry of [0.35, 0.65]){
                ctx.beginPath(); ctx.ellipse(cx,cy,r,r*ry,0,0,Math.PI*2); ctx.stroke();
            }
            // Equator slightly brighter
            ctx.globalAlpha=0.45; ctx.lineWidth=0.9;
            ctx.beginPath(); ctx.ellipse(cx,cy,r,r*0.08,0,0,Math.PI*2); ctx.stroke();

            ctx.globalAlpha=1;

            // Name
            const name=demoMask(cloud.d.name||cloud.d.asnName||'WAN');
            ctx.fillStyle=greyed?C.textMuted:C.textSec;
            ctx.font=`500 ${G.nameFont}px ${FONT}`;
            ctx.textAlign='center'; ctx.textBaseline='top';
            ctx.fillText(name,cx,cy+r+8);

            // RTT badge
            let rttTxt='';
            if(cloud.d.rttAvgMs!=null)rttTxt+=`${cloud.d.rttAvgMs.toFixed(1)} ms`;
            if(cloud.d.lossPercent&&cloud.d.lossPercent>0)rttTxt+=(rttTxt?' / ':'')+`${cloud.d.lossPercent.toFixed(1)}% loss`;
            if(rttTxt){
                ctx.fillStyle=C.textMuted;
                ctx.font=`${G.rateFont-1}px ${FONT}`;
                ctx.fillText(rttTxt,cx,cy+r+22);
            }
        }
    }

    // ---- Node drawing ----

    _drawAllNodes(ctx,n){
        // VirtualHub: show as compact label with member count, skip children
        if(n.d.kind===NK.VirtualHub){
            this._drawHubNode(ctx,n);
            return;
        }
        this._drawInfraNode(ctx,n);
        for(const c of n.infra)this._drawAllNodes(ctx,c);
        for(const c of n.clients.slice(0,G.maxClients)){if(this._isNodeVisible(c))this._drawClientNode(ctx,c);}
        if(n.clients.length>G.maxClients){
            const last=n.clients[G.maxClients-1];
            ctx.fillStyle=C.textMuted;
            ctx.font=`${G.rateFont}px ${FONT}`;
            ctx.textAlign='left'; ctx.textBaseline='middle';
            ctx.fillText(`+${n.clients.length-G.maxClients}`,last.x+G.clientR+10,last.y);
        }
    }

    _drawHubNode(ctx,n){
        const x=n.x,y=n.y,color=C.virtualHub;
        const memberCount=n.infra.length+n.clients.length;
        const r=10;

        // Small ring
        ctx.strokeStyle=color; ctx.lineWidth=2; ctx.globalAlpha=0.6;
        ctx.beginPath(); ctx.arc(x,y,r,0,Math.PI*2); ctx.stroke();
        ctx.fillStyle=withAlpha(color,0.1);
        ctx.beginPath(); ctx.arc(x,y,r-1,0,Math.PI*2); ctx.fill();
        ctx.globalAlpha=1;

        // Label - name may already include count from the server
        const name=demoMask(n.d.name||'Hub');
        const hasCount=/\(\d+\)/.test(name);
        const label=hasCount?name:memberCount>0?`${name} (${memberCount})`:name;
        const dn=label.length>28?label.slice(0,27)+'…':label;
        ctx.fillStyle=C.textSec;
        ctx.font=`${G.nameFont}px ${FONT}`;
        ctx.textAlign='center'; ctx.textBaseline='top';
        ctx.fillText(dn,x,y+r+4);
    }

    _drawInfraNode(ctx,n){
        const x=n.x, y=n.y, color=nodeClr(n.d.kind);
        const hw=G.boxW/2, hh=G.boxH/2;
        // Prefer the live/historic badge online state over the snapshot's build-time
        // value so the dimming tracks the timeline and live changes between rebuilds.
        const badge=flowData.getNodeBadges()?.[n.d.id];
        const online=badge?badge.online!==false:n.d.online;
        const op=online?1:0.35;

        // Glow
        ctx.fillStyle=withAlpha(color,0.07);
        this._roundRect(ctx,x-hw-5,y-hh-5,G.boxW+10,G.boxH+10,14);
        ctx.fill();

        // Card
        ctx.globalAlpha=op;
        ctx.fillStyle=C.cardBg;
        this._roundRect(ctx,x-hw,y-hh,G.boxW,G.boxH,10);
        ctx.fill();
        ctx.strokeStyle=color; ctx.lineWidth=1.5;
        this._roundRect(ctx,x-hw,y-hh,G.boxW,G.boxH,10);
        ctx.stroke();

        // Icon
        const iSz=G.iconSize;
        const modelKey=n.d.model?.toLowerCase().replace(/ /g,'-');
        const img=modelKey?this._images.get(modelKey):null;
        if(img){
            ctx.drawImage(img,x-iSz/2,y-iSz/2,iSz,iSz);
        } else {
            ctx.fillStyle=withAlpha(color,0.2);
            const s=iSz/2-6;
            this._roundRect(ctx,x-s,y-s,s*2,s*2,6);
            ctx.fill();
            ctx.fillStyle=color;
            ctx.font=`600 18px ${FONT}`;
            ctx.textAlign='center'; ctx.textBaseline='middle';
            ctx.fillText((n.d.name||'D').charAt(0).toUpperCase(),x,y);
        }
        ctx.globalAlpha=1;

        // Name label
        const name=demoMask(n.d.name||n.d.model||'');
        if(name){
            const dn=name.length>24?name.slice(0,23)+'…':name;
            ctx.font=`500 ${G.nameFont}px ${FONT}`;
            const tw=ctx.measureText(dn).width+12;
            const ly=y+hh+5+G.nameFont/2;
            ctx.fillStyle=C.labelBg;
            this._roundRect(ctx,x-tw/2,ly-8,tw,16,4);
            ctx.fill();
            ctx.fillStyle=C.text;
            ctx.textAlign='center'; ctx.textBaseline='middle';
            ctx.fillText(dn,x,ly);
        }

        // Rate labels (stored for dynamic update)
        n._rateY=y+hh+28;
    }

    _drawClientNode(ctx,n){
        const cs=flowData.getClientStats()?.[n.d.id];
        const band=cs?.band??n.d.band;
        const x=n.x, y=n.y, color=nodeClr(n.d.kind,band);
        const badge=flowData.getNodeBadges()?.[n.d.id];
        const online=badge?badge.online!==false:n.d.online;
        const r=G.clientR; let op=online?0.7:0.2;
        // Fade the client in after a roam re-attach so it doesn't pop at its new spot.
        if(n._roamFadeUntil){
            const t=1-Math.max(0,(n._roamFadeUntil-performance.now())/ROAM_FADE_MS);
            if(t>=1)n._roamFadeUntil=null; else op*=t;
        }

        ctx.globalAlpha=op;
        if(n.d.kind===NK.WifiClient){
            ctx.fillStyle=color;
            ctx.beginPath(); ctx.arc(x,y,r,0,Math.PI*2); ctx.fill();
            ctx.strokeStyle='rgba(255,255,255,0.5)'; ctx.lineWidth=0.8;
            ctx.beginPath();
            ctx.moveTo(x-2.5,y-0.5);
            ctx.quadraticCurveTo(x,y-4,x+2.5,y-0.5);
            ctx.stroke();
        } else {
            const s=r*0.9;
            ctx.fillStyle=color;
            this._roundRect(ctx,x-s,y-s+0.5,s*2,s*1.5,1.5);
            ctx.fill();
            ctx.beginPath(); ctx.moveTo(x,y+s*0.5+0.5); ctx.lineTo(x,y+s+1);
            ctx.strokeStyle=color; ctx.lineWidth=1.2; ctx.stroke();
        }
        ctx.globalAlpha=1;

        // Name label
        const name=demoMask(n.d.name||n.d.ip||'');
        if(name){
            const dn=name.length>32?name.slice(0,31)+'…':name;
            ctx.fillStyle=C.textMuted;
            ctx.font=`${G.clientFont}px ${FONT}`;
            ctx.textAlign='center'; ctx.textBaseline='top';
            ctx.fillText(dn,x,y+r+3);
        }
    }

    // A VirtualHub groups several wired clients sharing one physical switch port
    // (a server's VLAN sub-interfaces, etc.). Its own throughput is the parent
    // switch -> hub port link (direction-correct), not the summed member leaf links.
    // Prefer that upstream link; fall back to summing members only when it has no
    // rate. down=downstream / up=upstream on every WiredClient link, so no flip.
    _virtualHubRates(nodeId){
        let upDown=0,upUp=0,upHas=false,sumDown=0,sumUp=0,sumHas=false;
        for(const e of this._edges){
            const lk=e.lk;
            if(lk.toNodeId!==nodeId&&lk.fromNodeId!==nodeId)continue;
            const r=this._liveRates[lk.portKey]||this._liveRates[lk.id];
            if(!r)continue;
            const dn=r.downstreamBps||0,up=r.upstreamBps||0;
            if(lk.toNodeId===nodeId){upDown+=dn;upUp+=up;upHas=true;}
            else{sumDown+=dn;sumUp+=up;sumHas=true;}
        }
        if(upHas)return{down:upDown,up:upUp,any:upDown>0||upUp>0};
        if(sumHas)return{down:sumDown,up:sumUp,any:sumDown>0||sumUp>0};
        return{down:0,up:0,any:false};
    }

    // ---- Rate labels on links + nodes ----

    _drawRateLabels(ctx){
        const badges=flowData.getNodeBadges();
        const THRESH=RATE_THRESH;

        // Node rate labels
        const drawNodeRate=(n)=>{
            if(n._rateY){
                let downBps=0,upBps=0,any=false;
                const b=badges?.[n.d.id];
                const hasFab=b&&(b.fabricIngressBps!=null||b.fabricEgressBps!=null);
                const hasAgg=b&&(b.aggregateInBps!=null||b.aggregateOutBps!=null);

                if(hasFab){downBps=b.fabricIngressBps||0;upBps=b.fabricEgressBps||0;any=downBps>0||upBps>0;}
                else if(hasAgg){
                    if(n.d.kind===NK.AP){downBps=b.aggregateOutBps||0;upBps=b.aggregateInBps||0;}
                    else{downBps=b.aggregateInBps||0;upBps=b.aggregateOutBps||0;}
                    any=downBps>0||upBps>0;
                }

                if(any&&(downBps>100000||upBps>100000)){
                    ctx.font=`${G.rateFont}px ${FONT}`;
                    const dTxt='↓'+formatBps(downBps), uTxt='↑'+formatBps(upBps);
                    const tw=ctx.measureText(dTxt+'  '+uTxt).width+12;
                    ctx.fillStyle=C.labelBg;
                    this._roundRect(ctx,n.x-tw/2,n._rateY-8,tw,16,4);
                    ctx.fill();
                    ctx.textBaseline='middle';
                    ctx.textAlign='right'; ctx.fillStyle=C.downstream;
                    ctx.fillText(dTxt,n.x-4,n._rateY);
                    ctx.textAlign='left'; ctx.fillStyle=C.upstream;
                    ctx.fillText(uTxt,n.x+4,n._rateY);
                }
            }
            for(const c of n.infra)drawNodeRate(c);
        };
        if(this._root)drawNodeRate(this._root);

        // Link rate labels
        ctx.font=`${G.rateFont}px ${FONT}`;
        for(const e of this._edges){
            if(e._x1==null||e._isCl||e._isWan)continue;
            const r=this._liveRates[e.lk.portKey]||this._liveRates[e.lk.id];
            if(!r)continue;
            const dn=r.downstreamBps??0,up=r.upstreamBps??0;
            if(dn>THRESH||up>THRESH){
                // Place on child's vertical segment below the capacity label
                const midY=(e._y1+e._y2)/2+(e._midYOff||0);
                const mx=e._x2;
                const my=midY+38;
                const dTxt='↓'+(dn>0?formatBps(dn):'0 bps');
                const uTxt='↑'+(up>0?formatBps(up):'0 bps');
                const tw=ctx.measureText(dTxt+' '+uTxt).width+14;
                ctx.fillStyle=C.labelBg;
                this._roundRect(ctx,mx-tw/2,my-8,tw,16,4);
                ctx.fill();
                ctx.textBaseline='middle';
                ctx.textAlign='right'; ctx.fillStyle=C.downstream;
                ctx.fillText(dTxt,mx-4,my);
                ctx.textAlign='left'; ctx.fillStyle=C.upstream;
                ctx.fillText(uTxt,mx+4,my);
            }
        }

        // Client rate labels (same style as node/link labels)
        ctx.font=`${G.rateFont}px ${FONT}`;
        for(const e of this._edges){
            if(e._x1==null||!e._isCl)continue;
            const child=e.tn||e.fn;
            if(!child||!this._isNodeVisible(child))continue;
            const r=this._liveRates[e.lk.portKey]||this._liveRates[e.lk.id];
            if(!r)continue;
            const dn=r.downstreamBps??0,up=r.upstreamBps??0;
            if(dn>THRESH||up>THRESH){
                const cx=child.x;
                const cy=child.y+G.clientR+24;
                const dTxt='↓'+formatBps(dn), uTxt='↑'+formatBps(up);
                const tw=ctx.measureText(dTxt+'  '+uTxt).width+12;
                ctx.fillStyle=C.labelBg;
                this._roundRect(ctx,cx-tw/2,cy-8,tw,16,4);
                ctx.fill();
                ctx.textBaseline='middle';
                ctx.textAlign='right'; ctx.fillStyle=C.downstream;
                ctx.fillText(dTxt,cx-4,cy);
                ctx.textAlign='left'; ctx.fillStyle=C.upstream;
                ctx.fillText(uTxt,cx+4,cy);
            }
        }
    }

    // ---- Utility ----

    _roundRect(ctx,x,y,w,h,r){
        ctx.beginPath();
        ctx.moveTo(x+r,y);
        ctx.lineTo(x+w-r,y);
        ctx.quadraticCurveTo(x+w,y,x+w,y+r);
        ctx.lineTo(x+w,y+h-r);
        ctx.quadraticCurveTo(x+w,y+h,x+w-r,y+h);
        ctx.lineTo(x+r,y+h);
        ctx.quadraticCurveTo(x,y+h,x,y+h-r);
        ctx.lineTo(x,y+r);
        ctx.quadraticCurveTo(x,y,x+r,y);
        ctx.closePath();
    }

    // ---- Animation loop ----

    _animate(){
        const now=performance.now();
        const dt=Math.min((now-this._lastFrame)/1000,0.1);
        this._lastFrame=now;

        if(this._liveOnly||!flowData.isPaused()){
            for(const s of this._streams)s.advance(dt);
        }
        // Clients live on the cached static layer; force it to redraw while a roam
        // fade is in flight so the fade-in actually animates.
        if(this._roamFadeUntil&&now<this._roamFadeUntil)this._needsStaticRedraw=true;
        this._draw();

        this._animId=requestAnimationFrame(()=>this._animate());
    }
}

// ---- Module exports ----
let _inst=null;

export async function mount(containerId,opts){
    if(_inst){_inst.dispose();_inst=null;}
    const container=document.getElementById(containerId);
    if(!container)return;
    _inst=new LanFlowMap2D(container,opts);
    if(opts?.liveOnly)_inst._liveOnly=true;
    await _inst.start();
}

export function unmount(){
    if(_inst){_inst.dispose();_inst=null;}
}

export function startDataPolling(){
    if(_inst)_inst._liveOnly=true;
    flowData.startPolling();
}

export function stopDataPolling(){
    flowData.stopPolling();
    if(_inst)_inst._liveOnly=false;
}
