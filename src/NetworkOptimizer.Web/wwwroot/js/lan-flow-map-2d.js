// 2D hierarchical LAN topology diagram - Canvas 2D renderer.
// Subscribes to lan-flow-data.js (published by the 3D map) so there are
// zero duplicate API calls. GPU-composited canvas for smooth particle animation.

// KEEP IN SYNC: lan-flow-map.js imports the same module. Both must use the same ?v= or they get separate instances.
import * as flowData from './lan-flow-data.js?v=18';
import { opaqueBounds, logoFit } from './image-bounds.js?v=1';

function demoMask(text) {
    const dm = window.DemoMask;
    return (dm?.isEnabled() && dm.maskString) ? (dm.maskString(text) ?? text) : text;
}

// ---- Color palette (matches 3D map) ----
const C = {
    bg:           '#1c2128',
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
// How long the arrival focus ring shows once its node exists (mount option focusClient), and
// how long to wait for the node before giving up.
const FOCUS_MS = 5000;
const FOCUS_GIVE_UP_MS = 30000;
// Signal color arrives per node, blended on the server's per-band ramp. Nothing about the curve
// is decided here - a palette in the browser is how the map drifted from the gauges before.
const LK = { Uplink:0, WiredClient:1, WifiClient:2, Wan:3, Transit:4, MeshBackhaul:5 };
// Every color a Stream is built with (_initStreams): the particle loop fills one path per entry.
const STREAM_COLORS = [C.downstream, C.upstream];
const CT = { Solid:0, PathProxy:1, Unresolved:2 }; // LanCloudTier

// ---- Layout geometry ----
const G = {
    tierGap:     170,
    clientTierGap:165,
    cloudGap:    220,
    infraGap:    90,
    clientCellW: 145,
    clientCellH: 55,
    // Left to right, a stacked client sits straight below the one above, not staggered sideways,
    // so the pitch has to clear an icon client's name and rate label.
    clientCellHz: 64,
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
// A dot under this on-screen radius (device px) is drawn as a square, outside the batched path.
const DOT_PATH_MIN_PX = 1.5;
const DOT_TINY_PAD_PX = 0.16;
// Half-extents of the box a client can draw into (32-character name, rate label below).
const CLIENT_CULL_X = 150;
const CLIENT_CULL_Y = 60;
const FONT = 'Inter, system-ui, -apple-system, sans-serif';

// ---- Helpers ----

function hexRgb(h) { return [parseInt(h.slice(1,3),16), parseInt(h.slice(3,5),16), parseInt(h.slice(5,7),16)]; }
function rgbHex(r,g,b) { return '#'+[r,g,b].map(c=>Math.round(Math.max(0,Math.min(255,c))).toString(16).padStart(2,'0')).join(''); }
function lerp(a,b,t) { return a+(b-a)*t; }
function lerpColor(a,b,t) { const [r1,g1,b1]=hexRgb(a),[r2,g2,b2]=hexRgb(b); return rgbHex(lerp(r1,r2,t),lerp(g1,g2,t),lerp(b1,b2,t)); }
function bandClr(b) { return b==='2.4'?C.band24:b==='5'?C.band5:b==='6'?C.band6:null; }
// The band to paint a client's link with. The snapshot's link band is frozen at build
// time (i.e. "now"), while the dot and the tooltip both read the band at the scrubbed
// instant - so during playback the pipe disagreed with the dot it connects to. Live
// ticks carry no client stats, so this falls back to the snapshot value untouched.
function edgeBand(e) {
    const clientNode = e && (isClient(e.tn?.d?.kind) ? e.tn : isClient(e.fn?.d?.kind) ? e.fn : null);
    const cs = clientNode ? flowData.getClientStats()?.[clientNode.d.id] : null;
    return cs?.band ?? e?.lk?.band;
}
function nodeClr(k,b) {
    if(k===NK.Gateway)return C.gateway; if(k===NK.Switch)return C.switchNode;
    if(k===NK.AP)return C.ap; if(k===NK.WiredClient)return C.wiredClient;
    if(k===NK.WifiClient)return bandClr(b)||C.wifiClient;
    if(k===NK.Cloud)return C.cloud; if(k===NK.VirtualHub)return C.virtualHub;
    return C.textMuted;
}
function isInfra(k) { return k<=NK.AP||k===NK.VirtualHub; }
function isClient(k) { return k===NK.WiredClient||k===NK.WifiClient; }
// Opens an app page from the map, stamped with the tab's site, through Blazor's router so the page
// it opens has Back (setting location.href is a full load that starts history over).
function navigate(url) {
    const u=window.noSiteContext?window.noSiteContext.stampUrl(url):url;
    if(window.__navigate)window.__navigate(u); else window.location.href=u;
}

// Opens Live View with a device picked in its Port Statistics table, for a page without the
// table. The key matches port-stats-table.js's HANDOFF_KEY.
function openPortStatsOnLiveView(mac){
    try{sessionStorage.setItem('portStatsSelect',mac);}
    catch(e){console.debug('[lan-flow-map-2d] session storage unavailable; Live View opens unfiltered',e);}
    navigate('/monitoring?tab=live');
}

// Duration of the fade-in applied to a client that gets re-attached to a different
// AP during historic playback (roam), so it doesn't pop at its new position.
const ROAM_FADE_MS = 350;
// How long nodes, links and the fitted camera take to slide to a new layout.
const LAYOUT_MS = 450;
// Hover grow: how long the ease takes each way, and the size the icon reaches per object type.
// Only the icon (or dot, or globe) grows; cards, rings, and labels keep their size and place.
const HOVER_MS = 160;
const HOVER_GROW = {device: 1.08, hub: 1.15, client: 1.15, cloud: 1.08};
// A pan or zoom left alone this long, on a view still showing REFIT_SHARE of the map, re-fits again.
const REFIT_IDLE_MS = 30_000;
const REFIT_SHARE = 0.95;
/** How long the first frame waits for client icons before drawing dots in their place. */
const ICON_PRELOAD_MS = 1500;

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
//
// Each is written in the top-down frame: out of the parent, across at the midpoint, into the child.
// Left-to-right is that same path with both axes swapped, so `hz` mirrors the inputs going in and
// mirrors the result coming back rather than duplicating the geometry.

// Emits to a canvas context with both axes swapped, so strokeOrtho can draw its vertical-frame path
// into a horizontal layout untouched.
// A context that scales every alpha by `a`, for a node fading in or out. The draw functions set
// globalAlpha themselves, so an alpha set once before the call would be overwritten.
function fadedCtx(ctx,a){
    ctx.globalAlpha=a;
    return new Proxy(ctx,{
        get(t,k){const v=t[k];return typeof v==='function'?v.bind(t):v;},
        set(t,k,v){t[k]=k==='globalAlpha'?v*a:v;return true;},
    });
}

const axisSwap=(ctx)=>({
    beginPath:()=>ctx.beginPath(),
    stroke:()=>ctx.stroke(),
    moveTo:(a,b)=>ctx.moveTo(b,a),
    lineTo:(a,b)=>ctx.lineTo(b,a),
    quadraticCurveTo:(a,b,c,d)=>ctx.quadraticCurveTo(b,a,d,c),
});

function orthoLen(x1,y1,x2,y2,midYOff,hz) {
    if(hz)return orthoLen(y1,x1,y2,x2,midYOff,false);
    if(Math.abs(x1-x2)<0.5)return Math.abs(y2-y1);
    const {midY,ax,top,bot,c1,c2,ok}=orthoShape(x1,y1,x2,y2,midYOff);
    if(!ok)return Math.abs(midY-y1)+ax+Math.abs(y2-midY);
    return (top-c1)+(bot-c2)+Math.PI/2*(c1+c2)+(ax-c1-c2);
}

// `out`, when given, receives the point instead of a new object: the particle loop calls this
// for every dot on every frame.
// The shape of an ortho link: down (or up) to midY, across, on to the end, with a rounded corner at
// each turn. Either run may be up the screen, and either end run may be zero, which leaves an L
// with one rounded corner (a carrier's uplink ends flush on the carrier's side). c1/c2 are 0 where
// there is no turn; ok is false only when midY falls outside the two ends (drawn square).
function orthoShape(x1,y1,x2,y2,midYOff){
    const midY=(y1+y2)/2+(midYOff||0);
    const dx=x2-x1,s=dx>0?1:-1,ax=Math.abs(dx),vs=y2>=y1?1:-1;
    const top=(midY-y1)*vs,bot=(y2-midY)*vs,R=G.cornerR;
    const ok=top>-0.5&&bot>-0.5;
    let c1=0,c2=0;
    if(ok){
        if(top>=1&&bot>=1)c1=c2=Math.min(R,ax/2,top,bot);
        else if(top>=1)c1=Math.min(R,ax,top);
        else if(bot>=1)c2=Math.min(R,ax,bot);
        if(c1<1)c1=0; if(c2<1)c2=0;
    }
    return {midY,s,ax,vs,top:Math.max(0,top),bot:Math.max(0,bot),c1,c2,ok};
}

function orthoAt(x1,y1,x2,y2,t,midYOff,hz,out) {
    if(hz){const p=orthoAt(y1,x1,y2,x2,t,midYOff,false,out);const px=p.x;p.x=p.y;p.y=px;return p;}
    const o=out||{x:0,y:0};
    if(Math.abs(x2-x1)<0.5){o.x=x1;o.y=y1+(y2-y1)*t;return o;}
    const {midY,s,ax,vs,top,bot,c1,c2,ok}=orthoShape(x1,y1,x2,y2,midYOff);
    if(!ok){
        const a=Math.abs(midY-y1),b=Math.abs(y2-midY),tot=a+ax+b;let d=t*tot;
        const v1=midY>=y1?1:-1,v2=y2>=midY?1:-1;
        if(d<=a){o.x=x1;o.y=y1+v1*d;return o;}d-=a;
        if(d<=ax){o.x=x1+s*d;o.y=midY;return o;}d-=ax;
        o.x=x2;o.y=midY+v2*d;return o;
    }
    // Quadratic beziers matching strokeOrtho's corners.
    const s1=top-c1,a1=Math.PI/2*c1,s3=ax-c1-c2,a2=Math.PI/2*c2,s5=bot-c2;
    let d=t*(s1+a1+s3+a2+s5);
    if(d<=s1){o.x=x1;o.y=y1+vs*d;return o;}d-=s1;
    if(d<=a1&&a1>0){
        const bt=d/a1,u=1-bt;
        o.x=u*u*x1+2*u*bt*x1+bt*bt*(x1+s*c1); o.y=u*u*(midY-vs*c1)+2*u*bt*midY+bt*bt*midY;
        return o;
    }d-=a1;
    if(d<=s3){o.x=x1+s*(c1+d);o.y=midY;return o;}d-=s3;
    if(d<=a2&&a2>0){
        const bt=d/a2,u=1-bt;
        o.x=u*u*(x2-s*c2)+2*u*bt*x2+bt*bt*x2; o.y=u*u*midY+2*u*bt*midY+bt*bt*(midY+vs*c2);
        return o;
    }d-=a2;
    o.x=x2;o.y=midY+vs*(c2+Math.min(d,s5));return o;
}
const _dotPt={x:0,y:0};

function strokeOrtho(ctx,x1,y1,x2,y2,midYOff,hz) {
    if(hz)return strokeOrtho(axisSwap(ctx),y1,x1,y2,x2,midYOff,false);
    ctx.beginPath();
    if(Math.abs(x1-x2)<0.5){ctx.moveTo(x1,y1);ctx.lineTo(x2,y2);ctx.stroke();return;}
    const {midY,s,vs,c1,c2,ok}=orthoShape(x1,y1,x2,y2,midYOff);
    ctx.moveTo(x1,y1);
    if(!ok){ctx.lineTo(x1,midY);ctx.lineTo(x2,midY);ctx.lineTo(x2,y2);ctx.stroke();return;}
    ctx.lineTo(x1,midY-vs*c1);
    if(c1>0)ctx.quadraticCurveTo(x1,midY,x1+s*c1,midY);
    ctx.lineTo(x2-s*c2,midY);
    if(c2>0)ctx.quadraticCurveTo(x2,midY,x2,midY+vs*c2);
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
        this.pathLen=orthoLen(edge._x1,edge._y1,edge._x2,edge._y2,this.midYOff,edge._hz);
        this.slots=[];
        for(let i=0;i<MAX_DOTS;i++)this.slots.push({t:-1,size:0});
        // Dots in flight. An idle stream with none is skipped by advance and by the draw.
        this.live=0;
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
            this.live=steadyState;
        }
    }
    advance(dt){
        if(this.density===0&&this.live===0)return;
        // Emission: density²*12 dots/sec, jittered ±20% (3D uses ±40% but
        // shorter 2D links need tighter spacing to avoid visible gaps)
        this.spawnAcc+=this.density*this.density*EMIT_MAX*dt;
        while(this.spawnAcc>=1){
            this.spawnAcc-=(0.8+Math.random()*0.4);
            for(const sl of this.slots){if(sl.t<0){sl.t=this.dir>0?0:1;sl.size=this.dotSize;this.live++;break;}}
        }
        for(const sl of this.slots){
            if(sl.t<0)continue;
            sl.t+=this.velNorm*dt*this.dir;
            if(sl.t>1||sl.t<0){sl.t=-1;this.live--;}
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
        this._clientIcons=new Map();

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
        // Optional per-node overlay map (Firmware Rollout): id -> {color,badge,pulse,dim}.
        // Distinct from _overlays, which is this map's own visibility toggles.
        this._nodeOverlays=null;
        // A node to pick out for a moment on arrival: {mac, until}. Set by mount(focusClient).
        this._focus=null;
        // Host-page chrome switches (Firmware Rollout hides what it does not use).
        this._hideOverlayControls=false;
        this._hideFilter=false;
        this._hideVirtualHubs=false;
        this._hideClouds=false;
        this._hideWiredClients=false;
        this._hideWifiClients=false;
        this._hideHelp=false;
        this._hideRates=false;
        this._hideScrubber=false;
    }

    setOverlays(map){
        this._nodeOverlays=map&&Object.keys(map).length?map:null;
        this._needsStaticRedraw=true;
    }

    async start(){
        // Standalone (dashboard) mounts have no 3D map to reset stale playback
        // state a previous Monitoring session left in the shared store, so do it
        // here. On the Monitoring page the 3D map (the playback authority) has
        // already reset the store - and may have re-entered historic for a
        // deep-linked timestamp - so a full mount must never force it back live.
        if(this._liveOnly&&flowData.isPaused())flowData.publishPlayState(false,'live');
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
                        this._maybeRearmFit();
                        const prev=this._captureLayout();
                        // Detect infrastructure change (devices, not just client churn)
                        const infraCount=(nodes)=>(nodes||[]).filter(n=>n.kind<=3||n.kind===6).length;
                        const prevInfra=infraCount(this._snapshot?.nodes);
                        const newInfra=infraCount(s.nodes);
                        if(newInfra!==prevInfra){
                            this._buildLayout(s);
                            this._loadImages(s).then(()=>{this._needsStaticRedraw=true;});
                            this._refitIfFitted();
                        }else{
                            // Client churn or same topology: update data in place
                            const rebuilt=this._updateSnapshotData(s);
                            this._snapshot=s;
                            // Kinds are cached off the snapshot, and this path replaces it without
                            // a layout rebuild - so clients that joined or left resolve stale.
                            this._nodeKinds=null;
                            this._needsStaticRedraw=true;
                            if(rebuilt)this._refitIfFitted();
                        }
                        this._beginTransition(prev);
                    }
                    this._refreshTooltip();
                }
            }else if(ev==='live'){
                Object.assign(this._liveRates,flowData.getLiveRates());
                // Which clients were connected changes as you scrub, and only clients the layout
                // placed have coordinates - so a client coming back mid-playback would otherwise
                // draw at the origin with a degenerate link until something else forced a rebuild.
                if(this._presenceChanged())this._relayout();
                this._applyClientAssoc();
                this._updateStreamRates();
                this._updateCloudStats();
                this._needsStaticRedraw=true;
                this._refreshTooltip();
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
        if(this._visObs)this._visObs.disconnect();
        if(this._onKeyDown)document.removeEventListener('keydown',this._onKeyDown);
        if(this._isFullscreen)(this._fsTarget||this._el).classList.remove('lan-flow-map-fullscreen');
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
        // Rate and link-speed labels: they sit above the particles, so they cannot share the
        // static layer, and they change only when it does.
        this._labelCanvas=document.createElement('canvas');

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
        // A key press: the fullscreen button the click left focused would show its focus ring, so Esc drops the focus.
        this._onKeyDown=(e)=>{if(e.key==='Escape'&&this._isFullscreen){this._toggleFullscreen();if(document.activeElement?.tagName==='BUTTON')document.activeElement.blur();}};
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
            });
        });
        if(!this._hideFilter)this._el.appendChild(filter);

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
            });
            ctrlBody.appendChild(row);
        }
        if(!this._hideOverlayControls)this._el.appendChild(controls);

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
            else if(a==='fit')this._fitAll(true);
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
                <div class="lan-flow-map-help-row"><span>Open Client</span><span class="kbd">Double-click</span></div>
                <div class="lan-flow-map-help-row"><span>Open Port Statistics</span><span class="kbd">Double-click</span></div>
                <div class="lan-flow-map-help-row"><span>Open an AP's Client Stats</span><span class="kbd">Double-click</span></div>
                <div class="lan-flow-map-help-row"><span>Open a modem's Cellular Stats</span><span class="kbd">Double-click</span></div>
                <div class="lan-flow-map-help-row"><span>Open ISP Health</span><span class="kbd">Double-click</span></div>${scrubRows}
                <div class="lan-flow-map-help-row"><span>Fullscreen</span><span class="kbd">Esc</span> to exit</div>`;
        help.appendChild(helpBody);
        helpTitle.addEventListener('click',()=>helpBody.classList.toggle('is-collapsed'));
        if(!this._hideHelp)this._el.appendChild(help);

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
        if(!this._hideScrubber)this._el.appendChild(status);
        this._modeBadge=modeBadge;

        // Mirror scrubber (synced from 3D map via shared data store).
        // Interactions forward to the 3D map's instance.
        const scrubber=document.createElement('div');
        scrubber.className='lan-flow-map-scrubber';
        scrubber.dataset.tour='live-view-timeline';
        scrubber.innerHTML=`
            <div class="lan-flow-map-scrubber-row">
                <button class="lan-flow-map-scrubber-playpause" data-role="playpause" type="button" aria-label="Pause"><svg viewBox="0 0 12 12" width="10" height="10" fill="currentColor" aria-hidden="true"><rect x="2" y="1.500" width="2.800" height="9" rx="0.500"/><rect x="7.200" y="1.500" width="2.800" height="9" rx="0.500"/></svg></button>
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
            if(this._hideScrubber){scrubber.remove();return;}
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
        // The wheel zooms after the pointer rests on the map or a click (wheel-gate.js); always in fullscreen.
        this._wheelGate=window.WheelGate?.attach(this._el,{exempt:()=>(this._fsTarget||this._el).classList.contains('lan-flow-map-fullscreen')});
        canvas.addEventListener('pointerdown',(e)=>this._onDown(e));
        canvas.addEventListener('pointermove',(e)=>this._onMove(e));
        canvas.addEventListener('pointerup',(e)=>this._onUp(e));
        canvas.addEventListener('dblclick',(e)=>this._onDoubleClick(e));
        canvas.addEventListener('pointerleave',(e)=>{this._onUp(e);if(this._pointers.size===0&&e.pointerType==='mouse')this._hideTooltip();});

        // Resize
        this._resizeObs=new ResizeObserver(()=>this._resize());
        this._resizeObs.observe(this._el);
        this._resize();
        // Off screen (Live View stacks both maps) or in a hidden page, nothing is drawn.
        this._onScreen=true;
        this._visObs=new IntersectionObserver((entries)=>{for(const e of entries)this._onScreen=e.isIntersecting;});
        this._visObs.observe(canvas);
    }

    _resize(){
        const rect=this._canvas.getBoundingClientRect();
        // Capped like the 3D map's: past 2 the canvas costs more per frame than the eye can tell.
        this._dpr=Math.min(window.devicePixelRatio||1,2);
        this._cw=rect.width; this._ch=rect.height;
        this._canvas.width=rect.width*this._dpr;
        this._canvas.height=rect.height*this._dpr;
        this._staticCanvas.width=this._canvas.width;
        this._staticCanvas.height=this._canvas.height;
        this._labelCanvas.width=this._canvas.width;
        this._labelCanvas.height=this._canvas.height;
        this._needsStaticRedraw=true;

        // A tree drawn top-down grows wide and shallow, which is the wrong shape for a portrait
        // panel. Running depth left to right stacks it instead. Keyed on the panel, not the screen:
        // the map does not always fill the window.
        const hz=this._cw>0&&this._ch>0&&this._cw<this._ch;
        if(hz!==this._hz){
            this._hz=hz;
            // No slide here: every node changes axis, and the fit below moves the camera anyway.
            // The grow-only fit box still holds the other axis's extent, so it starts over.
            if(this._root){this._fitBox=null;this._relayout(false);this._fitAll();return;}
        }
        if(this._isFitted)this._fitAll();
    }

    // ---- Pan / Zoom ----

    _screenToWorld(sx,sy){
        return{x:(sx-this._cw/2)/this._scale+this._ox, y:(sy-this._ch/2)/this._scale+this._oy};
    }

    _onWheel(e){
        // Before the wheel gate opens, the wheel scrolls the page.
        if(this._wheelGate&&!this._wheelGate.armed)return;
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
        this._isFitted=false; this._userCamAt=performance.now();
        this._needsStaticRedraw=true;
    }

    _onDown(e){
        this._touch=e.pointerType!=='mouse';
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
        this._touch=e.pointerType!=='mouse';
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
            this._isFitted=false; this._userCamAt=performance.now();
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
            this._isFitted=false; this._userCamAt=performance.now();
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
                // Touch never synthesizes dblclick on every browser, so pair the taps here.
                const last=this._lastTap, now=performance.now();
                this._lastTap={t:now,x:e.clientX,y:e.clientY};
                if(last&&now-last.t<350&&Math.hypot(e.clientX-last.x,e.clientY-last.y)<24){
                    this._lastTap=null;
                    this._onDoubleClick(e);
                }
            }
        }
        this._tapStart=null;
    }

    // Frame a node with its context: the device it hangs from and that device's clients, so
    // the viewer sees where it sits, not one dot on an empty canvas. The user's own framing
    // from here on (this clears the fitted state, so churn does not snap the view back).
    _zoomToNode(n){
        // A device is framed as itself, what hangs off it, and the ONE uplink above it - the
        // link under test is the one going up, so it has to be on screen. Only that hop: the
        // full path to the gateway spans so many tiers that the fit stops zooming in at all
        // and the view pans off the device it was sent to.
        const isDev=n.d.kind===NK.Gateway||n.d.kind===NK.Switch||n.d.kind===NK.AP;
        let par=null;
        for(const cand of this._treeMap.values()){
            if(cand.clients?.includes(n)||cand.infra?.includes(n)){par=cand;break;}
        }
        const box=isDev?n:(par||n);
        const pts=[{x:box.x,y:box.y,hw:G.boxW/2,hh:G.boxH/2}];
        for(const c of (box.clients||[]))if(this._isNodeVisible(c))pts.push({x:c.x,y:c.y,hw:G.clientCellW/2,hh:G.clientCellH/2});
        if(isDev){
            for(const c of (box.infra||[]))if(this._isNodeVisible(c))pts.push({x:c.x,y:c.y,hw:G.boxW/2,hh:G.boxH/2});
            if(par)pts.push({x:par.x,y:par.y,hw:G.boxW/2,hh:G.boxH/2});
        }
        if(box!==n)pts.push({x:n.x,y:n.y,hw:G.clientCellW/2,hh:G.clientCellH/2});
        let l=Infinity,r=-Infinity,t=Infinity,b=-Infinity;
        for(const p of pts){l=Math.min(l,p.x-p.hw);r=Math.max(r,p.x+p.hw);t=Math.min(t,p.y-p.hh);b=Math.max(b,p.y+p.hh);}
        const pad=40;
        // On desktop the scrubber bar overlays the bottom of the stage (see _fitAll), so top-down
        // the usable height and the bottom edge sit above it; left-to-right nothing lands there.
        const barPx=!this._hz&&this._scrubberEl&&this._scrubberEl.parentElement===this._el?this._scrubberEl.offsetHeight+4:0;
        const w=Math.max(1,r-l+pad*2), h=Math.max(1,b-t+pad*2);
        // Short of a tight fit, so the neighbors have air around them.
        this._scale=Math.max(this._scale,Math.min(this._cw/w,Math.max(1,this._ch-barPx)/h,2.5)/1.5);
        // The client sits at the far edge of the tree's depth - the bottom top-down, the right
        // left-to-right - so it goes to that edge of the view and the context fills the rest.
        // The edge inset, in screen pixels at the chosen scale: top-down the client's whole cell,
        // since its label hangs below the dot; left-to-right the dot plus room for half a label,
        // which is centered under it, so the client sits close to the edge.
        const edgeX=G.clientR*this._scale+61, edgeY=G.clientCellH*this._scale+16;
        // A device sits in the middle of its own subtree; only a leaf goes to the edge.
        if(isDev){this._ox=(l+r)/2; this._oy=(t+b)/2;}
        else if(this._hz){this._ox=n.x-(this._cw/2-edgeX)/this._scale; this._oy=(t+b)/2;}
        else{this._ox=(l+r)/2; this._oy=n.y-(this._ch/2-edgeY-barPx)/this._scale;}
        this._isFitted=false; this._userCamAt=performance.now();
        this._needsStaticRedraw=true;
    }

    _fitAll(force){
        if(!this._root)return;
        // The first load fits again once the device images land, which would undo a focus zoom
        // taken a frame earlier; only the Fit button overrides an active focus.
        if(this._focus?.zoomed&&!force)return;
        // The fit is taken from final positions, so a slide in flight finishes first.
        if(this._trans){this._stepTransition(Infinity);this._dropTransition();}
        this._calcBounds(true);
        // The fitted box only ever grows. A client leaving shrinks the tree, and re-fitting to it
        // zooms and re-centres the whole map around one node's departure. Fit resets it.
        const box={x:this._bx,y:this._by,w:this._bw,h:this._bh};
        if(force||!this._fitBox)this._fitBox=box;
        else{
            const r=Math.max(this._fitBox.x+this._fitBox.w,box.x+box.w);
            const b=Math.max(this._fitBox.y+this._fitBox.h,box.y+box.h);
            this._fitBox.x=Math.min(this._fitBox.x,box.x);
            this._fitBox.y=Math.min(this._fitBox.y,box.y);
            this._fitBox.w=r-this._fitBox.x;
            this._fitBox.h=b-this._fitBox.y;
        }
        this._bx=this._fitBox.x; this._by=this._fitBox.y;
        this._bw=this._fitBox.w; this._bh=this._fitBox.h;
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
        this._scrubberEls.playPause.innerHTML=paused?'<svg viewBox="0 0 12 12" width="10" height="10" fill="currentColor" aria-hidden="true"><path d="M3 1.500v9l7.500-4.500z"/></svg>':'<svg viewBox="0 0 12 12" width="10" height="10" fill="currentColor" aria-hidden="true"><rect x="2" y="1.500" width="2.800" height="9" rx="0.500"/><rect x="7.200" y="1.500" width="2.800" height="9" rx="0.500"/></svg>';
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

    // Links inherit their endpoints' visibility: hiding a node leaves no dangling edge.
    // Resolved from the snapshot's node kinds, not the layout tree - a hub or cloud edge
    // carries no tree node, so tree refs alone let its link survive the hidden node.
    //
    // Never judge an infra endpoint by _isNodeVisible: the name and band filters narrow which
    // CLIENTS are drawn while infra is always drawn, so asking it about a switch erased every
    // infra link the moment a filter was typed.
    _isEdgeVisible(e){
        for(const id of [e.lk?.fromNodeId, e.lk?.toNodeId]){
            if(!id) continue;
            const kind=this._nodeKind(id);
            if(kind===NK.VirtualHub&&this._hideVirtualHubs) return false;
            if(kind===NK.WiredClient&&this._hideWiredClients) return false;
            if(kind===NK.WifiClient&&this._hideWifiClients) return false;
            if(kind===NK.Cloud&&this._hideClouds) return false;
            if(kind!==NK.WiredClient&&kind!==NK.WifiClient) continue;
            const tn=this._treeMap.get(id);
            if(tn&&!this._isNodeVisible(tn)) return false;
        }
        return true;
    }

    _nodeKind(id){
        if(!this._nodeKinds){
            this._nodeKinds=new Map();
            for(const n of this._snapshot?.nodes||[])this._nodeKinds.set(n.id,n.kind);
        }
        return this._nodeKinds.get(id);
    }

    _isNodeVisible(n){
        const k=n.d.kind;
        // The snapshot carries the clients connected NOW, so playback has to be told who was
        // connected THEN - otherwise a client that is up today is drawn at every past instant.
        if(isClient(k)){
            const present=flowData.getPresentClients();
            const measured=flowData.getMeasuredClients();
            // Hide only what we can actually see coming and going. A client that writes no
            // telemetry at all - a camera behind a device bridge has no switch port, so its
            // counters never land anywhere - is unknowable, and stays drawn as it always was.
            if(present&&measured&&measured.has(n.d.id)&&!present.has(n.d.id))return false;
        }
        if(k===NK.VirtualHub&&this._hideVirtualHubs)return false;
        if(k===NK.WiredClient&&this._hideWiredClients)return false;
        if(k===NK.WifiClient&&this._hideWifiClients)return false;
        if(k===NK.WifiClient){
            if(!this._overlays.wifiClients)return false;
            const nBand=flowData.getClientStats()?.[n.d.id]?.band??n.d.band;
            if(nBand&&!this._filter.bands[nBand])return false;
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

    _isCloudVisible(){return !this._hideClouds&&this._overlays.clouds;}

    _zoomBy(factor){
        this._scale=Math.max(0.05,Math.min(10,this._scale*factor));
        this._isFitted=false; this._userCamAt=performance.now();
        this._needsStaticRedraw=true;
    }

    _toggleFullscreen(){
        this._isFullscreen=!this._isFullscreen;
        const el=this._fsTarget||this._el;
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
        const sx=e.clientX-rect.left, sy=e.clientY-rect.top;
        // A WAN cloud opens that WAN's ISP Health report, named as every other link names it -
        // the primary included, since the report remembers the WAN it was last left on.
        if(!this._hideClouds){
            const w=this._screenToWorld(sx,sy);
            const cloud=this._clouds.find(c=>Math.hypot(w.x-c.x,w.y-c.y)<=G.cloudR);
            if(cloud){
                const wan=cloud.d.wanInterface;
                const url='/monitoring?tab=isp-health'+(wan?'&wan='+encodeURIComponent(wan):'');
                navigate(url);
                return;
            }
        }
        const hit=this._nodeAt(sx,sy);
        if(!hit)return;
        const d=hit.d;
        // A monitored cellular modem opens its series in Cellular Stats, as the Dashboard card does;
        // its ports say little. A Gateway with a modem inside keeps Port Statistics.
        if(d.cellularModemId!=null&&d.kind!==NK.Gateway){
            navigate(`/monitoring?tab=cellular&modem=${encodeURIComponent(d.cellularModemId)}`);
            return;
        }
        // An Access Point opens Wi-Fi Optimizer's Client Stats, filtered to its clients.
        if(d.kind===NK.AP&&d.mac){
            navigate(`/wifi-optimizer?tab=client&ap=${encodeURIComponent(d.mac)}`);
            return;
        }
        // Switches and gateways scroll to the port stats table and isolate that device. The
        // Dashboard's card has no table, so it opens Live View with the device picked there.
        if(d.kind===NK.Switch||d.kind===NK.Gateway){
            if(d.mac&&window.__portStatsTable){
                // The table is under the map, so fullscreen would hide the jump.
                if(this._isFullscreen)this._toggleFullscreen();
                window.__portStatsTable.selectDevice(d.mac);
                document.getElementById('port-stats-card')?.scrollIntoView({behavior:'smooth',block:'start'});
            }else if(d.mac&&this._liveOnly){
                openPortStatsOnLiveView(d.mac);
            }
            return;
        }
        // A shared-port hub carries the IP of its busiest interface, so it opens like a client.
        if(d.kind!==NK.WifiClient&&d.kind!==NK.WiredClient&&d.kind!==NK.VirtualHub)return;
        if(!d.ip)return;
        // Wi-Fi clients land on the Signal tab; wired clients go to the default tab.
        const tab=d.kind===NK.WifiClient?'&tab=signal':'';
        const url=`/client-dashboard?ip=${encodeURIComponent(d.ip)}${tab}`;
        navigate(url);
    }

    _hitTest(sx,sy){
        const hit=this._nodeAt(sx,sy);
        if(hit){this._hoverSx=sx;this._hoverSy=sy;}

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
        // WAN globes carry no tooltip, but they grow on hover like the nodes.
        const w=this._screenToWorld(sx,sy);
        this._hoverCloud=!hit&&this._isCloudVisible()?this._clouds.find(c=>Math.hypot(w.x-c.x,w.y-c.y)<=G.cloudR)||null:null;
    }

    // ---- Hover grow ----
    // Whatever is under the pointer eases up to HOVER_GROW of its size and back down when the
    // pointer leaves. Each object keeps its own progress (0..1), so a quick pass across a row leaves a
    // short wake instead of snapping. Keyed by node id, never held on the node: a client joining or
    // leaving rebuilds the tree with new node objects, and progress on the old one restarted the
    // grow from nothing, a pulse under a pointer that never moved.
    _stepHover(dt){
        const prog=this._hoverProg||(this._hoverProg=new Map());
        const targets=new Set();
        if(this._hoverNode)targets.add(this._hoverNode.d.id);
        if(this._hoverCloud)targets.add(this._hoverCloud.d.id);
        for(const id of targets)if(!prog.has(id))prog.set(id,0);
        if(!prog.size)return;
        const step=this._reducedMotion()?1:dt*1000/HOVER_MS;
        let moved=false;
        for(const[id,t]of prog){
            const target=targets.has(id)?1:0;
            // Settled: skip, or the shrink branch below steps a full-size icon down every frame.
            if(t===target)continue;
            const next=target>t?Math.min(1,t+step):Math.max(0,t-step);
            if(next!==t){prog.set(id,next);moved=true;}
            if(next===0&&target===0)prog.delete(id);
        }
        // Only while something is still easing: a node held at full size needs no redraw.
        if(moved)this._needsStaticRedraw=true;
    }

    // The scale an object's icon draws at: smoothstep over its hover progress.
    _hoverScale(o,grow){
        const t=this._hoverProg?.get(o.d.id)||0;
        return t>0?1+(grow-1)*t*t*(3-2*t):1;
    }

    // Draws fn scaled by s around (x,y).
    _scaledAt(ctx,x,y,s,fn){
        if(s===1){fn();return;}
        ctx.save();
        ctx.translate(x,y); ctx.scale(s,s); ctx.translate(-x,-y);
        fn();
        ctx.restore();
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
        // An MLO backhaul reads per link; the single reading is just the link the child measures.
        if(d.mloLinks?.length&&!cs?.signalDbm){for(const l of d.mloLinks)rows.push([`Signal (${l.band} GHz)`,`${l.signalDbm} dBm`]);}
        else if(signalDbm)rows.push(['Signal',`${signalDbm} dBm`]);
        const cellQ=cs?.cellularSignalQuality??d.cellularSignalQuality;
        if(cellQ!=null)rows.push(['Signal Quality',`${cellQ}%`]);
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
        }else if(isFab&&b&&(b.fabricIngressBps!=null||b.fabricEgressBps!=null
            ||b.aggregateInBps!=null||b.aggregateOutBps!=null)){
            if(b.fabricIngressBps!=null||b.fabricEgressBps!=null){
                inBps=b.fabricIngressBps||0;outBps=b.fabricEgressBps||0;any=true;
            }else{
                inBps=b.aggregateInBps||0;outBps=b.aggregateOutBps||0;any=true;
            }
        }else{
            // A fabric node whose badge carries no figures sums its own links instead. A UDB
            // classifies as a Switch but is deliberately left out of the fabric sum (its single
            // directional downlink series is not a switch fabric), and live has no badge for it
            // at all - so without this, playback showed nothing where live shows a rate.
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
        const wiredSpeed=cs?.wiredLinkSpeedMbps??d.wiredLinkSpeedMbps;
        if(wiredSpeed)rows.push(['Link speed',formatSpeed(wiredSpeed)]);
        else if(cs?.phyTxKbps||cs?.phyRxKbps||d.phyTxKbps||d.phyRxKbps){
            // Device perspective: download (↓) is the AP's TX to a Wi-Fi client, upload
            // (↑) is the AP's RX. A mesh uplink's Tx/Rx is the reverse, so swap.
            // Playback's own rate wins over the snapshot's, as band and signal above do -
            // reading d alone showed today's negotiated rate at every past instant.
            const pTx=cs?.phyTxKbps??d.phyTxKbps, pRx=cs?.phyRxKbps??d.phyRxKbps;
            const downKbps=isMeshUplink?pRx:pTx;
            const upKbps=isMeshUplink?pTx:pRx;
            const dl=downKbps?`↓${formatSpeed(Math.round(downKbps/1000))}`:'';
            const ul=upKbps?`↑${formatSpeed(Math.round(upKbps/1000))}`:'';
            rows.push([d.isMloMesh?'Link speed (MLO)':'Link speed',`${dl}${dl&&ul?'  ':''}${ul}`]);
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

        // What a double-click does here, if anything (mirrors _onDoubleClick).
        const gesture=this._touch?'Double-tap':'Double-click';
        let hint='';
        if(d.cellularModemId!=null&&d.kind!==NK.Gateway)hint=`${gesture} for Stats`;
        else if(d.kind===NK.AP&&d.mac)hint=`${gesture} for Clients`;
        else if((d.kind===NK.Switch||d.kind===NK.Gateway)&&d.mac&&(window.__portStatsTable||this._liveOnly))hint=`${gesture} for Port Statistics`;
        else if((d.kind===NK.WifiClient||d.kind===NK.WiredClient)&&d.ip)hint=`${gesture} for client details`;

        // An overlay tip (Firmware Rollout) leads: it is why the node is marked at all.
        const ovTip=this._nodeOverlays?.[d.id]?.tip;
        this._tooltip.innerHTML=
            `<div style="font-weight:600;margin-bottom:3px">${esc(m(d.name||d.mac||''))}</div>`
            +(ovTip?`<div style="margin-bottom:3px">${esc(String(ovTip))}</div>`:'')
            +rows.map(([k,v])=>`<div style="display:flex;justify-content:space-between;gap:12px"><span style="color:${C.textMuted};white-space:nowrap">${k}</span><span>${esc(String(v))}</span></div>`).join('')
            +(hint?`<div style="color:${C.textMuted};margin-top:4px">${hint}</div>`:'');
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

    // Re-render the open tooltip so its readings follow each tick instead of the hover that
    // opened it. A layout rebuild replaces the node objects, so resolve by id.
    _refreshTooltip(){
        if(!this._hoverNode)return;
        const node=this._treeMap.get(this._hoverNode.d.id);
        if(!node||!this._isNodeVisible(node)){this._hideTooltip();return;}
        this._hoverNode=node;
        this._showTooltip(node,this._hoverSx,this._hoverSy);
    }

    _hideTooltip(){
        this._hoverNode=null;
        this._hoverCloud=null;
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
        this._nodeKinds=null;

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
        this._carriers=this._findCarriers();
        this._contourLayout(root);
        this._assignAbsoluteXY(root,0,0);
        this._placeClouds();
        this._placeCarriers();
        if(this._carriers?.size)this._placeClouds(true);
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

    _relayout(animate=true){
        if(!this._root)return;
        this._maybeRearmFit();
        const prev=animate?this._captureLayout():this._dropTransition();
        this._spreadFactor=this._getSpreadFactor();
        this._carriers=this._findCarriers();
        this._contourLayout(this._root);
        this._assignAbsoluteXY(this._root,0,0);
        this._placeClouds();
        this._placeCarriers();
        if(this._carriers?.size)this._placeClouds(true);
        this._matchEdges();
        this._initStreams();
        this._calcBounds();
        this._updateStreamRates();
        this._needsStaticRedraw=true;
        this._refitIfFitted();
        this._beginTransition(prev);
    }

    // ---- Layout transitions ----
    // A layout change computes the final positions as before, then every node and the fitted
    // camera slide there from where they were drawn. Links are re-routed from the moving nodes
    // each frame, so particles ride them. A node that joins slides out of its parent and fades
    // in; one that leaves slides back into its parent and fades out as a ghost.

    // Calls fn(node, parent) for every node the layout placed, in the order _drawAllNodes draws.
    _walkPlaced(n,p,fn){
        fn(n,p);
        if(n.d.kind===NK.VirtualHub)return;
        for(const c of n.infra)this._walkPlaced(c,n,fn);
        for(const c of (n._visClients||[]))fn(c,n);
    }

    // A pan or zoom stops the re-fit. Once it has been left alone for a while on a view that still
    // shows nearly the whole map, the fit comes back, so a growing layout does not run off a view
    // nobody is steering. Run before the layout changes: after it, the overflow fails the share.
    _maybeRearmFit(){
        if(this._isFitted||!this._root)return;
        if(performance.now()-(this._userCamAt??0)<REFIT_IDLE_MS)return;
        this._calcView();
        let all=0,shown=0;
        this._walkPlaced(this._root,null,n=>{all++;if(this._inView(n.x,n.y,0,0))shown++;});
        if(all>0&&shown>=all*REFIT_SHARE)this._isFitted=true;
    }

    _reducedMotion(){
        try{return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches===true;}catch{return false;}
    }

    // Ends any transition where it stands and returns null (the "do not animate" capture).
    _dropTransition(){
        const tr=this._trans;
        if(tr)for(const it of tr.items)it.n._tAlpha=undefined;
        this._trans=null;
        return null;
    }

    // Where everything is drawn right now: the start of the next transition. Taken mid-transition
    // it is the in-between position, so a new change carries on from there instead of jumping.
    // Everything is then put at its destination: a change that rebuilds nothing would otherwise
    // leave a slide frozen where it stood (a roamed client stuck by its old AP until the next rebuild).
    _captureLayout(){
        const tr=this._trans, ghosts=tr?tr.ghosts:[];
        const motion=this._root&&!this._reducedMotion();
        const nodes=new Map(), clouds=new Map();
        if(motion){
            this._walkPlaced(this._root,null,(n,p)=>{
                const e=p?this._edges.find(e=>(e.fn===p&&e.tn===n)||(e.fn===n&&e.tn===p)):null;
                nodes.set(n.d.id,{x:n.x,y:n.y,tn:n,pid:p?.d.id,edge:e?{lk:e.lk,band:edgeBand(e)}:null});
            });
            for(const c of this._clouds)clouds.set(c.d.id,{x:c.x,y:c.y});
        }
        const cam={s:this._scale,ox:this._ox,oy:this._oy};
        if(tr){
            for(const it of tr.items){it.n.x=it.tx;it.n.y=it.ty;}
            for(const it of tr.clouds){it.c.x=it.tx;it.c.y=it.ty;}
            if(tr.cam){this._scale=tr.cam.to.s;this._ox=tr.cam.to.ox;this._oy=tr.cam.to.oy;}
            this._matchEdges();
        }
        this._dropTransition();
        return motion?{nodes,clouds,ghosts,cam}:null;
    }

    _beginTransition(prev){
        if(!prev||!this._root)return;
        const items=[], seen=new Set(), start=new Map();
        this._walkPlaced(this._root,null,(n,p)=>{
            seen.add(n.d.id);
            const o=prev.nodes.get(n.d.id);
            // A newcomer starts where its parent starts, so the two move as one.
            const from=o||(p&&start.get(p.d.id))||{x:n.x,y:n.y};
            start.set(n.d.id,from);
            if(!o||from.x!==n.x||from.y!==n.y)items.push({n,fx:from.x,fy:from.y,tx:n.x,ty:n.y,fade:!o});
        });
        const ghosts=[];
        // Ghosts of the transition this one interrupts keep going from where they are.
        for(const gh of prev.ghosts)if(!seen.has(gh.g.d.id))ghosts.push({...gh,fx:gh.g.x,fy:gh.g.y,a0:gh.g._tAlpha??1});
        for(const[id,o]of prev.nodes){
            if(seen.has(id))continue;
            const g=Object.assign(Object.create(Object.getPrototypeOf(o.tn)),o.tn,{_roamFadeUntil:null});
            ghosts.push({g,fx:o.x,fy:o.y,a0:1,pid:o.pid,edge:o.edge});
        }
        const clouds=[];
        for(const c of this._clouds){
            const o=prev.clouds.get(c.d.id);
            if(o&&(o.x!==c.x||o.y!==c.y))clouds.push({c,fx:o.x,fy:o.y,tx:c.x,ty:c.y});
        }
        const camTo={s:this._scale,ox:this._ox,oy:this._oy}, cf=prev.cam;
        const camMoves=cf.s!==camTo.s||cf.ox!==camTo.ox||cf.oy!==camTo.oy;
        if(!items.length&&!ghosts.length&&!clouds.length&&!camMoves)return;
        this._trans={start:performance.now(),items,ghosts,clouds,
            cam:camMoves?{from:cf,to:camTo,last:null}:null,seen};
        this._stepTransition(this._trans.start);
    }

    _stepTransition(now){
        const tr=this._trans;
        const t=Math.min(1,(now-tr.start)/LAYOUT_MS), k=1-Math.pow(1-t,3);
        for(const it of tr.items){
            it.n.x=lerp(it.fx,it.tx,k); it.n.y=lerp(it.fy,it.ty,k);
            it.n._tAlpha=it.fade&&t<1?t:undefined;
        }
        for(const it of tr.clouds){it.c.x=lerp(it.fx,it.tx,k); it.c.y=lerp(it.fy,it.ty,k);}
        // A ghost heads for its parent where the parent is still on the map (a ghost parent
        // counts, and comes first in the list), else it fades where it was.
        const ghostById=new Map();
        for(const gh of tr.ghosts){
            const par=ghostById.get(gh.pid)||(tr.seen.has(gh.pid)?this._treeMap.get(gh.pid):null);
            gh.par=par;
            gh.g.x=par?lerp(gh.fx,par.x,k):gh.fx;
            gh.g.y=par?lerp(gh.fy,par.y,k):gh.fy;
            gh.g._tAlpha=gh.a0*(1-t);
            ghostById.set(gh.g.d.id,gh.g);
        }
        const cam=tr.cam;
        if(cam){
            // A pan or zoom by the user mid-slide takes the camera back.
            const l=cam.last;
            if(l&&(l.s!==this._scale||l.ox!==this._ox||l.oy!==this._oy))tr.cam=null;
            else{
                this._scale=lerp(cam.from.s,cam.to.s,k);
                this._ox=lerp(cam.from.ox,cam.to.ox,k);
                this._oy=lerp(cam.from.oy,cam.to.oy,k);
                cam.last={s:this._scale,ox:this._ox,oy:this._oy};
            }
        }
        this._matchEdges();
        this._needsStaticRedraw=true;
        if(t>=1)this._trans=null;
    }

    // Leaving nodes and their links, drawn under the live nodes so they slide in behind the parent.
    _drawGhosts(ctx){
        const tr=this._trans;
        if(!tr)return;
        const hz=this._hz;
        for(const gh of tr.ghosts){
            const g=gh.g, a=g._tAlpha;
            if(!(a>0))continue;
            const cl=isClient(g.d.kind);
            if(gh.par&&gh.edge){
                const p=gh.par;
                const x1=hz?p.x+G.boxW/2:p.x, y1=hz?p.y:p.y+G.boxH/2;
                const inset=cl?G.clientR:G.boxH/2;
                const x2=hz?g.x-inset:g.x, y2=hz?g.y:g.y-inset;
                ctx.strokeStyle=pipeClr(0,gh.edge.band);
                ctx.lineWidth=pipeW(gh.edge.lk.capacityBps);
                ctx.lineCap='round';
                ctx.globalAlpha=(cl?0.3:0.5)*a;
                strokeOrtho(ctx,x1,y1,x2,y2,0,hz);
            }
            if(!this._inView(g.x,g.y,CLIENT_CULL_X,CLIENT_CULL_Y))continue;
            const fc=fadedCtx(ctx,a);
            if(cl)this._drawClientNode(fc,g);
            else if(g.d.kind===NK.VirtualHub)this._drawHubNode(fc,g);
            else this._drawInfraNode(fc,g);
        }
        ctx.globalAlpha=1;
    }

    // Draws a node through a faded context while it is fading in.
    _drawFading(ctx,n,draw){
        const a=n._tAlpha;
        if(a==null||a>=1){draw(ctx);return;}
        draw(fadedCtx(ctx,a));
        ctx.globalAlpha=1;
    }

    // Re-attach wifi clients to the AP they were on at the scrubbed instant (roam
    // playback). The historic update carries clientStats[clientId].apNodeId; in live
    // mode it's empty, so every client falls back to its snapshot parent (reset).
    // Only relayouts when an association actually changed, so steady live is free.
    /// Whether the set of clients present at this instant differs from the one the layout was
    /// built against. Cheap: a size and a membership check over client nodes only.
    _presenceChanged(){
        const present=flowData.getPresentClients();
        const measured=flowData.getMeasuredClients();
        const key=present?[...present].sort().join(',')+'|'+(measured?measured.size:0):'';
        if(key===this._presenceKey)return false;
        this._presenceKey=key;
        return true;
    }

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
            // Detach from wherever it actually is, not from where the bookkeeping says: a client
            // left in two parents' lists is laid out under one and has its edge matched from the
            // other, which draws a link clear across the map.
            let found=0;
            for(const[,p]of this._treeMap){
                const i=p.clients.indexOf(tn);
                if(i>=0){p.clients.splice(i,1);found++;}
            }
            if(found>1)console.warn('[lan-flow-map-2d] client was under',found,'parents:',tn.d.id);
            newParent.clients.push(tn);
            const edge=this._edges.find(e=>e.tn===tn||e.fn===tn);
            if(edge){if(edge.tn===tn)edge.fn=newParent;else edge.tn=newParent;}
            this._appliedAssoc.set(id,desired);
            tn._roamFadeUntil=now+ROAM_FADE_MS;
            changed=true;
        }
        // Mesh APs follow their historic parent too, carrying their subtree with them. The
        // backhaul link is snapshot-shared, so only the edge's endpoint moves, as for a client.
        for(const e of this._edges){
            if(e.lk.kind!==LK.MeshBackhaul||!e.tn)continue;
            const tn=e.tn,id=tn.d.id;
            const baseline=e.lk.fromNodeId;
            const desired=stats[id]?.apNodeId||baseline;
            const cur=this._appliedAssoc.get(id)||baseline;
            if(desired===cur)continue;
            const newParent=this._treeMap.get(desired);
            if(!newParent||isClient(newParent.d.kind))continue;
            // A parent inside the AP's own subtree would make a cycle; leave it where it is.
            if(this._inSubtree(tn,newParent))continue;
            for(const[,p]of this._treeMap){
                const i=p.infra.indexOf(tn);
                if(i>=0)p.infra.splice(i,1);
            }
            newParent.infra.push(tn);
            e.fn=newParent;
            this._appliedAssoc.set(id,desired);
            tn._roamFadeUntil=now+ROAM_FADE_MS;
            changed=true;
        }
        if(changed){this._roamFadeUntil=now+ROAM_FADE_MS;this._relayout();}
    }

    _inSubtree(root,n){
        for(const c of root.infra){
            if(c===n||this._inSubtree(c,n))return true;
        }
        return false;
    }

    // Compute contour (left/right extent at each depth relative to node x=0)
    // and store relative child offsets on the node.
    _contourLayout(n){
        this._contourLayoutInner(n);
        // A WAN carrier (_placeCarriers) leaves the kids and takes a reserved slot on its parent's
        // free side.
        if(this._carrierOf(n)){
            const c0=n._contour[0], slot=this._carrierSlotR();
            n._contour[0]=this._carrierSide()>0?{l:c0.l,r:Math.max(c0.r,slot)}:{l:Math.min(c0.l,-slot),r:c0.r};
        }
    }

    _contourLayoutInner(n){
        const cellCross=this._hz?G.clientCellHz:G.clientCellW;
        const selfW=isClient(n.d.kind)?cellCross:(this._hz?G.boxH:G.boxW)+40;
        n._isGrid=false;
        const visCl=n.clients.filter(c=>this._isNodeVisible(c));
        const nc=Math.min(visCl.length,G.maxClients);
        n._visClients=visCl.slice(0,G.maxClients);

        // VirtualHub: treat as a leaf (children won't be rendered)
        if(n.d.kind===NK.VirtualHub){
            const hubW=cellCross;
            n._contour=[{l:-hubW/2,r:hubW/2}];
            n._kidOffsets=[];
            n._kids=[];
            return;
        }

        const infra=this._carriers?.size?n.infra.filter(c=>!this._carriers.has(c)):n.infra;

        // Pure-client nodes (APs with only WiFi clients): compact grid
        if(infra.length===0&&nc>0){
            const gc=this._gridContour(nc,cellCross);
            n._isGrid=true;
            n._gridCols=gc.cols;
            // Contour: node at depth 0, grid rectangle at depth 1
            n._contour=[
                {l:-selfW/2,r:selfW/2},
                {l:gc.l,r:gc.r},
            ];
            n._kidOffsets=[];
            n._kids=[];
            return;
        }

        // Infra children; clients always use a grid (placeholder in the kids array)
        const kids=[...infra];
        if(nc>0){
            const gc=this._gridContour(nc,cellCross);
            n._isGrid=true;
            n._gridCols=gc.cols;
            const gp={_isGridPlaceholder:true,_contour:[{l:gc.l,r:gc.r}],_rows:gc.rows};
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
                // The client grid is one tier deep, so it fits a gap between two infra boxes that
                // deeper, wider levels forced apart - rather than always trailing the last subtree.
                // Three rows at most: a fourth reaches the tier where those subtrees' children sit.
                if(kids[i]._isGridPlaceholder&&kids[i]._rows<=3){
                    const slot=this._gridSlot(kids,offsets,i,cc[0],GAP);
                    if(slot!=null){offsets[i]=slot;continue;}
                }
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

        // Extremes, not first and last: a grid slotted into a gap is out of offset order.
        let firstL=Infinity,lastR=-Infinity;
        for(let i=0;i<kids.length;i++){
            firstL=Math.min(firstL,offsets[i]+kids[i]._contour[0].l);
            lastR=Math.max(lastR,offsets[i]+kids[i]._contour[0].r);
        }
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

    // Exact extent of a client honeycomb. Odd rows shift half a cell, so a full one reaches half a
    // cell past the even rows - a short one does not, and reserving the shift regardless left a
    // dead half-cell beside every grid whose last row was not full.
    _gridContour(nc,cellCross){
        const cols=Math.min(nc,G.clientCols);
        const rows=Math.ceil(nc/cols);
        const l=-cols*cellCross/2;
        let r=-Infinity;
        for(let row=0;row<rows;row++){
            const inRow=Math.min(cols,nc-row*cols);
            r=Math.max(r,l+(row%2)*cellCross/2+inRow*cellCross);
        }
        return{cols,rows,l,r};
    }

    // The first gap between two already-packed siblings' boxes that fits the grid, as an offset;
    // null when none does, in which case it packs after the last sibling as any kid would.
    _gridSlot(kids,offsets,i,gc,gap){
        const width=gc.r-gc.l;
        for(let j=0;j+1<i;j++){
            const from=offsets[j]+kids[j]._contour[0].r+gap;
            const to=offsets[j+1]+kids[j+1]._contour[0].l-gap;
            if(to-from>=width)return from-gc.l;
        }
        return null;
    }

    // The client honeycomb, centered on the parent's cross-axis position and running away from it
    // along the depth axis. Cell sizes swap with the axes: a stacked client needs a row's height,
    // not a column's width.
    _placeClientGrid(n,centerOff,nodeX,nodeY){
        const vc=n._visClients||[];
        const cols=n._gridCols;
        if(this._hz){
            const span=cols*G.clientCellHz;
            const top=centerOff-span/2;
            const startX=nodeX+G.clientTierGap;
            const stagger=G.clientCellHz/2;
            for(let i=0;i<vc.length;i++){
                const col=i%cols,row=Math.floor(i/cols);
                vc[i].y=top+col*G.clientCellHz+G.clientCellHz/2+(row%2)*stagger;
                vc[i].x=startX+row*G.clientCellW;
            }
            return;
        }
        const span=cols*G.clientCellW;
        const left=centerOff-span/2;
        const startY=nodeY+G.clientTierGap;
        const stagger=G.clientCellW/2;
        for(let i=0;i<vc.length;i++){
            const col=i%cols,row=Math.floor(i/cols);
            vc[i].x=left+col*G.clientCellW+G.clientCellW/2+(row%2)*stagger;
            vc[i].y=startY+row*G.clientCellH;
        }
    }

    _assignAbsoluteXY(n,absX,depth){
        const off=G.pad+80;
        const hz=this._hz;
        // The contour packer works in offset space, so orientation is only ever this mapping:
        // depth runs down the page, or across it.
        if(hz){n.y=absX;n.x=off+depth*G.tierGap;}
        else   {n.x=absX;n.y=off+depth*G.tierGap;}

        // Client grid: compact rows with honeycomb stagger
        if(n._isGrid&&(!n._kids||n._kids.length===0)){
            this._placeClientGrid(n,absX,n.x,n.y);
            return;
        }

        const kids=n._kids||[];
        const offsets=n._kidOffsets||[];
        const sf=this._spreadFactor||1;
        for(let i=0;i<kids.length;i++){
            if(kids[i]._isGridPlaceholder){
                this._placeClientGrid(n,absX+offsets[i]*sf,n.x,n.y);
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
                tn.d.signalBars=n.signalBars;
                tn.d.signalColor=n.signalColor;
                tn.d.mloLinks=n.mloLinks;
                tn.d.cellularSignalQuality=n.cellularSignalQuality;
                // A client first drawn from the live overlay, or before its DHCP lease, has no
                // address yet; the console's later reading is what makes it openable.
                if(isClient(n.kind)){
                    tn.d.ip=n.ip;
                    tn.d.name=n.name;
                    tn.d.ssid=n.ssid;
                    tn.d.network=n.network;
                    tn.d.isGuest=n.isGuest;
                }
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
        return clientsChanged;
    }

    // Clients joining, leaving, roaming or scrubbing all change the tree's extent, so part of it
    // can end up off-screen. Re-fit while the view is still the fitted one (a Fit press re-arms
    // it); once the user has zoomed or panned, that framing is theirs to keep - the same
    // _isFitted rule the filter and overlay toggles use. Unchanged bounds fit to the same frame.
    _refitIfFitted(){
        if(this._isFitted)this._fitAll();
    }

    // Where a label sits on an elbow: on one end's leg, `off` clear of the crossbar. Which
    // coordinate is the leg and which is the crossbar swaps with the layout, so every label anchor
    // goes through here rather than assuming the top-down frame.
    _elbowLabel(e,atSource,off){
        // A carrier's uplink L: its labels ride the long run toward the carrier, away from the
        // parent's own incoming labels, which they would otherwise line up with and read as a pair.
        if(e._carrierUp)return this._alongRun(e,0.8);
        // A WAN entering a carrier's side runs the globe's leg most of the way: label it by the globe.
        if(e._viaSide&&atSource)return e._hz?{x:e._x1+70,y:e._y1}:{x:e._x1,y:e._y1+60};
        const stagger=e._midYOff||0;
        if(e._hz){
            return{x:(e._x1+e._x2)/2+stagger+off, y:atSource?e._y1:e._y2};
        }
        return{x:atSource?e._x1:e._x2, y:(e._y1+e._y2)/2+stagger+off};
    }

    // Throughput sits on the long run, biased toward the child so it reads as belonging to the
    // device it feeds. Once depth runs sideways that run is the crossbar, the one vertical segment
    // on the path; in the top-down frame it stays on the child's leg.
    // A point a fraction of the way along an L's long run: the crossbar of an edge drawn sideways.
    _alongRun(e,f){
        const off=e._midYOff||0;
        return e._hz?{x:(e._x1+e._x2)/2+off,y:e._y1+(e._y2-e._y1)*f}
                    :{x:e._x1+(e._x2-e._x1)*f,y:(e._y1+e._y2)/2+off};
    }

    _rateLabel(e){
        if(e._carrierUp)return this._alongRun(e,0.55);
        if(e._hz){
            return{x:(e._x1+e._x2)/2+(e._midYOff||0), y:e._y1+(e._y2-e._y1)*0.75};
        }
        return this._elbowLabel(e,false,38);
    }

    // carriersPlaced: a second pass once _placeCarriers has run, ordering by where the carriers
    // landed rather than by their parents. It reuses the same slots, so no collision check changes.
    _placeClouds(carriersPlaced=false){
        if(!this._root||this._clouds.length===0)return;
        // Globes in the order of where their WANs land across the map: a WAN carried by a modem
        // lands at the modem, so its globe goes to that side and its line stays short.
        // Ties keep the server's order.
        const land=new Map();
        for(const e of this._edges){
            if(e.lk.kind!==LK.Wan)continue;
            const via=e.lk.viaNodeId?this._treeMap.get(e.lk.viaNodeId):null;
            const anchor=(via&&(carriersPlaced&&via.x!=null?via:(this._parentOf(via)||via)))||this._root;
            land.set(e.lk.fromNodeId,this._hz?anchor.y:anchor.x);
        }
        const rank=new Map(this._clouds.map((c,i)=>[c,i]));
        const gwCross=this._hz?this._root.y:this._root.x;
        const key=(c)=>land.get(c.d.id)??gwCross;
        this._clouds.sort((a,b)=>(key(a)-key(b))||(rank.get(a)-rank.get(b)));
        // WANs landing on the gateway take the slots nearest it; carried ones the rest, in order of
        // where they land. A carried WAN's line then never drops down the gateway's own column.
        const carried=new Set();
        for(const e of this._edges)if(e.lk.kind===LK.Wan&&e.lk.viaNodeId&&this._treeMap.get(e.lk.viaNodeId))carried.add(e.lk.fromNodeId);
        if(carried.size&&carried.size<this._clouds.length){
            const n=this._clouds.length, mid=(n-1)/2;
            const slots=[...Array(n).keys()];
            const nearGw=[...slots].sort((a,b)=>Math.abs(a-mid)-Math.abs(b-mid)||a-b);
            const gwClouds=this._clouds.filter(c=>!carried.has(c.d.id));
            const gwSlots=new Set(nearGw.slice(0,gwClouds.length));
            const order=new Array(n);
            gwClouds.forEach((c,i)=>{order[[...gwSlots].sort((a,b)=>a-b)[i]]=c;});
            const rest=slots.filter(s=>!gwSlots.has(s));
            this._clouds.filter(c=>carried.has(c.d.id)).forEach((c,i)=>{order[rest[i]]=c;});
            this._clouds.splice(0,n,...order);
        }
        const gx=this._root.x,gy=this._root.y;
        const total=this._clouds.length,sp=G.cloudGap;
        const stagger=45;
        // Upstream of the gateway along the depth axis, spread across the other one.
        if(this._hz){
            const sy=gy-((total-1)*sp)/2;
            const baseX=gx-G.tierGap*1.6;
            for(let i=0;i<total;i++){
                this._clouds[i].y=sy+i*sp;
                this._clouds[i].x=baseX+(1-i%2)*stagger;
            }
            return;
        }
        const sx=gx-((total-1)*sp)/2;
        const baseY=gy-G.tierGap*1.6;
        for(let i=0;i<total;i++){
            this._clouds[i].x=sx+i*sp;
            this._clouds[i].y=baseY+(1-i%2)*stagger;
        }
    }

    // ---- WAN carriers ----
    // A device that carries a WAN (a cellular modem: the WAN link's viaNodeId) is drawn between its
    // uplink and the WAN globe instead of in its uplink's row: beside the uplink, raised toward the
    // globes above the uplink's incoming bus. Only a leaf moves; one with devices or clients under
    // it stays in the tree and its WAN link enters its side (_matchEdges).

    _findCarriers(){
        const set=new Set();
        for(const e of this._edges){
            if(e.lk.kind!==LK.Wan||!e.lk.viaNodeId)continue;
            const c=this._treeMap.get(e.lk.viaNodeId);
            if(!c||c===this._root||c.infra.length>0||c.clients.length>0)continue;
            if(this._parentOf(c))set.add(c);
        }
        return set;
    }

    _parentOf(n){
        for(const p of this._treeMap.values())if(p.infra.includes(n))return p;
        return null;
    }

    // The carrier among a node's infra children, if any (one per parent is placed; a second stays put).
    _carrierOf(n){
        if(!this._carriers?.size)return null;
        for(const c of n.infra)if(this._carriers.has(c))return c;
        return null;
    }

    // A parent's one free side, where its carrier goes: top-down its right (the incoming link is on
    // top, the children below, the labels under the box); left to right its top (incoming on the
    // left, children on the right, labels below). The parent's contour reserves that side, so the L
    // joining them never runs through a sibling or the parent's own labels. +1 is right/below.
    _carrierSide(){return this._hz?-1:1;}
    // Cross-axis offset of a carrier from its parent, and the parent contour edge that reserves it.
    _carrierOffset(){return (this._hz?G.boxH:G.boxW)+80;}
    _carrierSlotR(){const s=this._hz?G.boxH:G.boxW;return this._carrierOffset()+s/2+20;}

    _placeCarriers(){
        if(!this._carriers?.size)return;
        const hz=this._hz, off=this._carrierOffset();
        for(const c of this._carriers){
            const p=this._parentOf(c);
            if(!p||p.x==null||this._carrierOf(p)!==c){this._carriers.delete(c);continue;}
            // The parent's incoming bus sits halfway to its own parent's tier (the root has none,
            // so it is half a tier toward the globes). Raised, the carrier and its labels sit just
            // above it on the parent's free side, a slot or two out; else level with the parent, in
            // the slot its contour reserved.
            const bus=(hz?p.x:p.y)-G.tierGap/2;
            const raised=bus-(hz?(G.boxW/2+44):(G.boxH/2+44))-8;
            const side=this._carrierSide();
            const at=(depth,extra)=>{
                const cross=(hz?p.y:p.x)+side*(off+extra);
                return hz?{x:depth,y:cross}:{x:cross,y:depth};
            };
            // A spot counts only if its box, the L to it, and the WAN line's drop onto it all clear
            // every other device: a free box at the end of a line through a switch is no better.
            const obs=this._carrierObstacles(p,c);
            let spot=null,leg=null;
            for(const extra of [0,110,220]){
                const pt=at(raised,extra);
                if(!this._carrierSpotFree(pt,c))continue;
                if(!this._segsClear(this._carrierDrop(pt),obs))continue;
                leg=this._carrierLegs(p,pt,true).find(l=>this._segsClear(l.segs,obs));
                if(leg){spot=pt;break;}
            }
            c._carrierRaised=!!spot;
            if(!spot){
                spot=at(hz?p.x:p.y,0);
                leg=this._carrierLegs(p,spot,false)[0];
                // Level, the WAN takes the top if its drop is clear, else the outer side.
                c._carrierTopEntry=this._segsClear(this._carrierDrop(spot),obs);
            }else{
                c._carrierTopEntry=true;
            }
            c.x=spot.x; c.y=spot.y;
            c._carrierSide=side;
            c._carrierLeg=leg?.leg;
        }
    }

    // The L from a parent's free side to its carrier at `pt`, as segments, one per bend it may take:
    // top-down the climb hugging the parent or hugging the carrier; left to right one shape (up out of
    // the parent's top, then across into the carrier's near side). Level, a straight line.
    _carrierLegs(p,pt,raised){
        const hz=this._hz, hw=G.boxW/2, hh=G.boxH/2;
        if(hz){
            if(!raised)return[{leg:null,segs:[[p.x,p.y-hh,p.x,pt.y+hh]]}];
            return[{leg:null,segs:[[p.x,p.y-hh,p.x,pt.y],[p.x,pt.y,pt.x+hw,pt.y]]}];
        }
        const ax=p.x+hw, bx=pt.x-hw;
        if(!raised)return[{leg:null,segs:[[ax,p.y,bx,p.y]]}];
        return[ax+12,bx-12].map(lx=>({leg:lx,segs:[[ax,p.y,lx,p.y],[lx,p.y,lx,pt.y],[lx,pt.y,bx,pt.y]]}));
    }

    // The WAN line's last stretch onto a carrier's top (its left side, left to right), halfway up to
    // the globe row: the part that would run through whatever sits above the carrier.
    _carrierDrop(pt){
        const hz=this._hz, r=this._root;
        if(hz){
            const gx=r.x-G.tierGap*1.6+G.cloudR, ex=pt.x-G.boxW/2;
            return[[ex,pt.y,(ex+gx)/2,pt.y]];
        }
        const gy=r.y-G.tierGap*1.6+G.cloudR, ey=pt.y-G.boxH/2;
        return[[pt.x,ey,pt.x,(ey+gy)/2]];
    }

    // Boxes and label bands of every placed device except the parent and its carrier.
    _carrierObstacles(p,c){
        const out=[];
        this._walkPlaced(this._root,null,(n)=>{
            if(n===p||n===c||n.x==null)return;
            if(isClient(n.d.kind)){out.push({x0:n.x-20,x1:n.x+20,y0:n.y-G.clientR-4,y1:n.y+G.clientR+38});return;}
            out.push({x0:n.x-G.boxW/2-4,x1:n.x+G.boxW/2+4,y0:n.y-G.boxH/2-4,y1:n.y+G.boxH/2+4});
            out.push({x0:n.x-70,x1:n.x+70,y0:n.y+G.boxH/2,y1:n.y+G.boxH/2+44});
        });
        return out;
    }

    // Whether axis-aligned segments [x1,y1,x2,y2] stay clear of every obstacle box.
    _segsClear(segs,obs){
        for(const[x1,y1,x2,y2]of segs){
            const s={x0:Math.min(x1,x2)-2,x1:Math.max(x1,x2)+2,y0:Math.min(y1,y2)-2,y1:Math.max(y1,y2)+2};
            if(obs.some(o=>o.x0<s.x1&&o.x1>s.x0&&o.y0<s.y1&&o.y1>s.y0))return false;
        }
        return true;
    }

    // Whether a carrier's box and labels at this spot clear every other placed node and globe.
    _carrierSpotFree(pt,self){
        const box=(x,y,w,up,down)=>({x0:x-w,x1:x+w,y0:y-up,y1:y+down});
        const me=box(pt.x,pt.y,75,G.boxH/2+4,G.boxH/2+44);
        const hit=(o)=>o.x0<me.x1&&o.x1>me.x0&&o.y0<me.y1&&o.y1>me.y0;
        let free=true;
        this._walkPlaced(this._root,null,(n)=>{
            if(!free||n===self||n.x==null)return;
            const r=isClient(n.d.kind)?box(n.x,n.y,G.clientCellW/2,G.clientR+4,G.clientR+38)
                :box(n.x,n.y,75,G.boxH/2+4,G.boxH/2+44);
            if(hit(r))free=false;
        });
        if(free&&this._isCloudVisible()){
            for(const cl of this._clouds)if(hit(box(cl.x,cl.y,G.cloudR+50,G.cloudR+4,G.cloudR+34))){free=false;break;}
        }
        return free;
    }

    _matchEdges(){
        const gw=this._root;
        if(!gw)return;
        const hz=this._hz;
        // Anchors leave the parent and enter the child on whichever axis depth runs along.
        const leadOut=(n)=>hz?{x:n.x+G.boxW/2,y:n.y}:{x:n.x,y:n.y+G.boxH/2};
        const leadIn=(n,inset)=>hz?{x:n.x-inset,y:n.y}:{x:n.x,y:n.y-inset};
        const gwIn=leadIn(gw,G.boxH/2);
        const STAGGER=6;

        // WAN cloud links - stagger by index
        const wanEdges=[];
        for(const cloud of this._clouds){
            const from=hz?{x:cloud.x+G.cloudR+1,y:cloud.y}:{x:cloud.x,y:cloud.y+G.cloudR+1};
            const edge=this._edges.find(e=>
                (e.lk.kind===LK.Wan||e.lk.kind===LK.Transit)
                &&(e.lk.fromNodeId===cloud.d.id||e.lk.toNodeId===cloud.d.id));
            // A WAN a cellular modem carries is drawn from that modem, not the gateway. A carrier
            // placed beside its uplink (_placeCarriers) takes it on top like any WAN link. One left in
            // the tree sits deep in it, so the link enters its side facing the globe: the globe's leg
            // runs to its height and turns once (_viaSide), instead of dropping through the devices above.
            const via=edge?.lk.viaNodeId?this._treeMap.get(edge.lk.viaNodeId):null;
            const viaOk=via&&via.x!=null&&via!==gw;
            const placed=viaOk&&this._carriers?.has(via);
            // A placed carrier takes it on top when _placeCarriers found that drop clear (always, when
            // raised), else on its outer side if the globe is on that side; with the globe across its
            // parent, the outer side would route through the parent, so the top it is. One left in the
            // tree takes the side facing the globe.
            const globeSide=hz?(from.y>=via?.y?1:-1):(from.x>=via?.x?1:-1);
            const viaSide=viaOk&&(!placed||(!via._carrierTopEntry&&globeSide===(via._carrierSide||1)));
            let to=gwIn;
            if(viaSide){
                const s=globeSide;
                to=hz?{x:via.x,y:via.y+s*G.boxH/2}:{x:via.x+s*G.boxW/2,y:via.y};
            }else if(viaOk){
                to=leadIn(via,G.boxH/2);
            }
            if(edge){edge._x1=from.x;edge._y1=from.y;edge._x2=to.x;edge._y2=to.y;edge._isWan=true;edge._hz=hz;edge._viaSide=!!viaSide;wanEdges.push(edge);}
        }
        const wanMid=(wanEdges.length-1)/2;
        for(let i=0;i<wanEdges.length;i++){
            const e=wanEdges[i];
            // The crossbar at the far end makes the last leg zero-length: one turn, into the side.
            e._midYOff=e._viaSide?(hz?(e._x2-e._x1)/2:(e._y2-e._y1)/2):(i-wanMid)*STAGGER;
        }

        // Tree links - stagger siblings from same parent
        const matchTree=(n)=>{
            // VirtualHub: only match the hub's own uplink, skip its children
            if(n.d.kind===NK.VirtualHub)return;

            const pOut=leadOut(n);
            const sibEdges=[];

            for(const c of n.infra){
                // VirtualHub renders as a small ring (r=10), not a full box
                const cIn=leadIn(c,c.d.kind===NK.VirtualHub?12:G.boxH/2);
                // By TN identity, as for clients below: a mesh AP re-parented in playback keeps its
                // snapshot link, and only the edge's endpoint follows the tree.
                const edge=this._edges.find(e=>(e.fn===n&&e.tn===c)||(e.fn===c&&e.tn===n));
                if(edge&&this._carriers?.has(c)){
                    // A WAN carrier beside its parent: one L out of the parent's free side, outside
                    // the sibling bundle. Top-down: right, then up into the carrier's near side, the
                    // climb hugging the parent so a long carrier name hangs clear of it. Left to
                    // right: up out of the parent's top, then across into the carrier's near side
                    // (a level carrier sits straight above, so the L is a straight line).
                    if(hz){
                        const a={x:n.x,y:n.y-G.boxH/2};
                        const b=c._carrierRaised?{x:c.x+G.boxW/2,y:c.y}:{x:c.x,y:c.y+G.boxH/2};
                        edge._x1=a.x;edge._y1=a.y;edge._x2=b.x;edge._y2=b.y;
                        edge._hz=false;edge._midYOff=c._carrierRaised?(b.y-a.y)/2:0;
                    }else{
                        const a={x:n.x+G.boxW/2,y:n.y};
                        const b={x:c.x-G.boxW/2,y:c.y};
                        edge._x1=a.x;edge._y1=a.y;edge._x2=b.x;edge._y2=b.y;
                        // The climb _placeCarriers checked clear (hugging the parent or the carrier).
                        edge._hz=true;edge._midYOff=(c._carrierLeg??(a.x+12))-(a.x+b.x)/2;
                    }
                    edge._isCl=false;edge._carrierUp=true;
                }else if(edge){edge._x1=pOut.x;edge._y1=pOut.y;edge._x2=cIn.x;edge._y2=cIn.y;edge._isCl=false;edge._hz=hz;edge._carrierUp=false;sibEdges.push(edge);}
                matchTree(c);
            }
            // The same set the layout just positioned. Reading n.clients here instead would set
            // edge coordinates from a node this pass never placed, leaving the line at its old
            // position while the node draws somewhere else.
            for(const c of (n._visClients||n.clients.slice(0,G.maxClients))){
                const cIn=leadIn(c,G.clientR);
                // Match the client's uplink edge by TN identity, not link ids: during
                // roam playback the client is re-parented to its historic AP, so the
                // edge must follow the tree without mutating the shared snapshot link.
                // A client has exactly one tree edge (its uplink), so this is unambiguous.
                const edge=this._edges.find(e=>e.tn===c||e.fn===c);
                if(edge){edge._x1=pOut.x;edge._y1=pOut.y;edge._x2=cIn.x;edge._y2=cIn.y;edge._isCl=true;edge._hz=hz;edge._band=edgeBand(edge);sibEdges.push(edge);}
            }

            // Stagger the crossbars of siblings that share the same parent. Sideways the crossbar
            // carries a label, so the bundle spreads as wide as the gap between the boxes allows.
            if(sibEdges.length>1){
                const mid=(sibEdges.length-1)/2;
                const step=hz?Math.min(24,Math.max(STAGGER,78/(sibEdges.length-1))):STAGGER;
                for(let i=0;i<sibEdges.length;i++)sibEdges[i]._midYOff=(i-mid)*step;
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
            const len=orthoLen(e._x1,e._y1,e._x2,e._y2,e._midYOff,e._hz);
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
        const CLIENT_UP=G.clientR+4, CLIENT_DOWN=G.clientR+38; // dot top ; icon + name + rate label
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
        // Client icons too, so the first frame has them instead of dots that swap in one by one.
        // Capped: a slow icon draws as a dot and appears when it lands.
        const icons=[];
        for(const n of snap.nodes){
            if((isClient(n.kind)||n.kind===NK.VirtualHub)&&n.iconUrl&&!this._clientIcons.has(n.iconUrl))icons.push(this._loadClientIcon(n.iconUrl).promise);
        }
        for(const c of snap.clouds||[]){
            if(c.logoUrl&&!this._clientIcons.has(c.logoUrl))icons.push(this._loadClientIcon(c.logoUrl).promise);
        }
        if(icons.length>0)promises.push(Promise.race([Promise.all(icons),new Promise(r=>setTimeout(r,ICON_PRELOAD_MS))]));
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

        // One path and one fill per color, not per dot. Dots follow a link's visibility (_vis,
        // set with the line in _drawAllLinks) and are skipped for a link outside the viewport.
        // Never batch the sub-pixel dots into the path: a path of thousands of tiny contours
        // rasterizes them away and the streams go dim at fit-all zoom. Those draw as squares,
        // which no eye can tell from a circle at that size. DOT_TINY_PAD_PX keeps their
        // brightness where a lone anti-aliased arc put it: an arc that small covers more than
        // its area.
        this._calcView();
        ctx.globalCompositeOperation='lighter';
        ctx.globalAlpha=0.85;
        const TAU=Math.PI*2, pxWorld=1/(this._scale*dpr);
        const tinyR=DOT_PATH_MIN_PX*pxWorld, tinyPad=DOT_TINY_PAD_PX*pxWorld;
        for(const color of STREAM_COLORS){
            ctx.fillStyle=color;
            ctx.beginPath();
            for(const s of this._streams){
                if(s.live===0||s.color!==color)continue;
                const e=s.edge;
                if(!e._vis||!this._edgeInView(e))continue;
                for(const sl of s.slots){
                    if(sl.t<0)continue;
                    const pt=orthoAt(e._x1,e._y1,e._x2,e._y2,sl.t,s.midYOff,e._hz,_dotPt);
                    if(sl.size<tinyR){
                        const h=(sl.size+tinyPad)*0.8862; // half-side of the square with the circle's area
                        ctx.fillRect(pt.x-h,pt.y-h,h*2,h*2);
                        continue;
                    }
                    ctx.moveTo(pt.x+sl.size,pt.y);
                    ctx.arc(pt.x,pt.y,sl.size,0,TAU);
                }
            }
            ctx.fill();
        }
        ctx.globalCompositeOperation='source-over';
        ctx.globalAlpha=1;

        // Overlay pulse rings (world coords, animated, so drawn per frame)
        if(this._nodeOverlays){
            const t=(performance.now()%1500)/1500;
            for(const id in this._nodeOverlays){
                const ov=this._nodeOverlays[id];
                if(!ov?.pulse)continue;
                const n=this._treeMap.get(id);
                if(!n||n.x==null)continue;
                const grow=8*t, hw=G.boxW/2+5+grow, hh=G.boxH/2+5+grow;
                ctx.strokeStyle=ov.color||C.textSec;
                ctx.globalAlpha=0.7*(1-t);
                ctx.lineWidth=2+2*t;
                this._roundRect(ctx,n.x-hw,n.y-hh,hw*2,hh*2,14+grow);
                ctx.stroke();
            }
            ctx.globalAlpha=1;
        }

        // Arrival focus: the same ring the overlay pulse draws, in the download blue, for a few
        // seconds around the node a deep link sent the viewer here to look at.
        if(this._focus){
            const now=performance.now();
            // Either node kind: a speed test's target is a client or a UniFi device, and the
            // ring below already draws a box for one and a circle for the other.
            let n=this._treeMap.get('cli-'+this._focus.mac)||this._treeMap.get('dev-'+this._focus.mac);
            // A VirtualHub member is never laid out or drawn (the hub subsumes its children),
            // so its TN sits at the constructor's (0,0) and the ring would land in empty
            // space. The hub is the member's on-screen representative.
            const hub=n&&n.d.parentId?this._treeMap.get(n.d.parentId):null;
            if(hub&&hub.d.kind===NK.VirtualHub)n=hub;
            // The clock starts when the node first exists, not at mount: the snapshot and the
            // layout arrive well after the page does. A node that never shows gives up.
            if(this._focus.until==null&&n&&n.x!=null&&this._isNodeVisible(n))this._focus.until=now+FOCUS_MS;
            if(now>=(this._focus.until??this._focus.giveUp))this._focus=null;
            else if(n&&n.x!=null&&this._isNodeVisible(n)){
                if(!this._focus.zoomed){this._focus.zoomed=true;this._zoomToNode(n);}
                const t=(now%1500)/1500, grow=8*t;
                const isDev=n.d.kind===NK.Gateway||n.d.kind===NK.Switch||n.d.kind===NK.AP;
                ctx.strokeStyle=C.downstream;
                ctx.globalAlpha=0.8*(1-t);
                ctx.lineWidth=2+2*t;
                if(isDev){
                    const hw=G.boxW/2+5+grow, hh=G.boxH/2+5+grow;
                    this._roundRect(ctx,n.x-hw,n.y-hh,hw*2,hh*2,14+grow);
                }else{
                    ctx.beginPath(); ctx.arc(n.x,n.y,G.clientR+5+grow,0,Math.PI*2);
                }
                ctx.stroke();
                ctx.globalAlpha=1;
            }
        }

        // Labels on top of everything (including particles)
        if(!this._hideRates){
            ctx.setTransform(1,0,0,1,0,0);
            ctx.drawImage(this._labelCanvas,0,0);
        }
    }

    // The viewport in world coordinates, for culling.
    _calcView(){
        const hw=this._cw/2/this._scale, hh=this._ch/2/this._scale;
        this._vx0=this._ox-hw; this._vx1=this._ox+hw;
        this._vy0=this._oy-hh; this._vy1=this._oy+hh;
    }

    _inView(x,y,mx,my){
        return x+mx>=this._vx0&&x-mx<=this._vx1&&y+my>=this._vy0&&y-my<=this._vy1;
    }

    // A link's path stays inside its endpoints' box, give or take the elbow offset and line width.
    _edgeInView(e){
        const m=Math.abs(e._midYOff||0)+8;
        return Math.max(e._x1,e._x2)+m>=this._vx0&&Math.min(e._x1,e._x2)-m<=this._vx1
            &&Math.max(e._y1,e._y2)+m>=this._vy0&&Math.min(e._y1,e._y2)-m<=this._vy1;
    }

    _drawLabelLayer(){
        const c=this._labelCanvas, ctx=c.getContext('2d');
        ctx.setTransform(1,0,0,1,0,0);
        ctx.clearRect(0,0,c.width,c.height);
        if(!this._root||this._hideRates)return;
        const dpr=this._dpr;
        ctx.setTransform(this._scale*dpr,0,0,this._scale*dpr,
            (this._cw/2-this._ox*this._scale)*dpr,
            (this._ch/2-this._oy*this._scale)*dpr);
        // Labels go down in priority order and each records its box; a link label that would land
        // on one already placed slides along its own run (_slideAlong) to the nearest clear spot.
        // Device boxes, names and globes first, then device and client rates, then link rates (the
        // live figures), then link speeds. One that fits where it is stays there.
        this._labelTaken=this._labelObstacles();
        this._drawRateLabels(ctx);
        this._drawLinkSpeedLabels(ctx);
    }

    // What a link label must not cover: every placed device box and name, client dot, and globe.
    _labelObstacles(){
        const out=[];
        if(!this._root)return out;
        this._walkPlaced(this._root,null,(n)=>{
            if(n.x==null)return;
            if(n._nameRect)out.push(n._nameRect);
            if(isClient(n.d.kind))out.push({x:n.x-G.clientR-2,y:n.y-G.clientR-2,w:G.clientR*2+4,h:G.clientR*2+4});
            else if(n.d.kind===NK.VirtualHub)out.push({x:n.x-12,y:n.y-12,w:24,h:24});
            else out.push({x:n.x-G.boxW/2,y:n.y-G.boxH/2,w:G.boxW,h:G.boxH});
        });
        if(this._isCloudVisible())for(const c of this._clouds)out.push({x:c.x-G.cloudR,y:c.y-G.cloudR,w:G.cloudR*2,h:G.cloudR*2});
        return out;
    }

    // A label's box covers or crowds one already placed. A link's own labels may sit tight (its speed
    // over its rate reads as one link's). Another link's label keeps a clear gap beside it, or two
    // links' labels side by side read as a pair; stacked, as siblings' labels on a crossbar are, they
    // read fine tight.
    _labelHits(r,owner){
        return(this._labelTaken||[]).some(t=>{
            const gx=owner&&t.owner&&t.owner!==owner?12:2, gy=2;
            return r.x<t.x+t.w+gx&&r.x+r.w>t.x-gx&&r.y<t.y+t.h+gy&&r.y+r.h>t.y-gy;
        });
    }

    // The nearest point to `lp` on the run of edge `e` it sits on where a w-by-h label covers
    // nothing already placed, stepping out both ways; `lp` itself when it fits or nothing does,
    // except on a WAN carrier's uplink, whose labels are dropped (null) rather than drawn over a
    // device: its rates repeat the carrier's own rate label, its speed is in the tooltip.
    _slideAlong(e,lp,w,h){
        const p=this._slideAlongInner(e,lp,w,h);
        if(e?._carrierUp&&p===lp&&this._labelHits({x:lp.x-w/2,y:lp.y-h/2,w,h},e))return null;
        return p;
    }

    _slideAlongInner(e,lp,w,h){
        const rect=(p)=>({x:p.x-w/2,y:p.y-h/2,w,h});
        if(!e||e._x1==null||!this._labelHits(rect(lp),e))return lp;
        const off=e._midYOff||0;
        // The path's corners, as strokeOrtho draws it (a sideways edge swaps the axes).
        const pts=e._hz
            ?(()=>{const mx=(e._x1+e._x2)/2+off;return[{x:e._x1,y:e._y1},{x:mx,y:e._y1},{x:mx,y:e._y2},{x:e._x2,y:e._y2}];})()
            :(()=>{const my=(e._y1+e._y2)/2+off;return[{x:e._x1,y:e._y1},{x:e._x1,y:my},{x:e._x2,y:my},{x:e._x2,y:e._y2}];})();
        let best=null,bd=Infinity;
        for(let i=0;i<3;i++){
            const a=pts[i],b=pts[i+1],len=Math.hypot(b.x-a.x,b.y-a.y);
            if(len<1)continue;
            const ux=(b.x-a.x)/len,uy=(b.y-a.y)/len;
            const t=Math.max(0,Math.min(len,(lp.x-a.x)*ux+(lp.y-a.y)*uy));
            const d=Math.hypot(a.x+ux*t-lp.x,a.y+uy*t-lp.y);
            if(d<bd){bd=d;best={a,ux,uy,len,t};}
        }
        if(!best)return lp;
        // Keep the label on its segment, clear of the corners it would otherwise hang past.
        const vertical=Math.abs(best.uy)>0.5;
        const m=(vertical?h:w)/2+4;
        const onRun=(t,perp)=>({
            x:best.a.x+best.ux*t+(lp.x-(best.a.x+best.ux*best.t))*Math.abs(best.uy)+(vertical?perp:0),
            y:best.a.y+best.uy*t+(lp.y-(best.a.y+best.uy*best.t))*Math.abs(best.ux)+(vertical?0:perp)});
        for(let k=1;k<=60;k++){
            const t=best.t+(k%2?-1:1)*Math.ceil(k/2)*6;
            if(t<m||t>best.len-m)continue;
            const p=onRun(t,0);
            if(!this._labelHits(rect(p),e))return p;
        }
        // A run too short for it (a level carrier's L): just off the run, either side, closest
        // first, rather than on top of a device.
        const across=(vertical?w:h)/2+6;
        const mid=Math.max(0,Math.min(best.len,best.t));
        for(const perp of [across,-across,across+18,-across-18]){
            for(let k=0;k<=12;k++){
                const t=mid+(k%2?-1:1)*Math.ceil(k/2)*6;
                if(t<0||t>best.len)continue;
                const p=onRun(t,perp);
                if(!this._labelHits(rect(p),e))return p;
            }
        }
        return lp;
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

        if(!this._root){this._drawLabelLayer();return;}
        this._calcView();

        // Links (pipes only)
        this._pendingLinkLabels=[];
        this._drawAllLinks(ctx);
        // Clouds (if overlay enabled)
        if(this._isCloudVisible())this._drawAllClouds(ctx);
        this._drawGhosts(ctx);
        // Infra + client nodes
        this._drawAllNodes(ctx,this._root);
        // After the nodes: label placement reads the rects they record.
        this._drawLabelLayer();
    }

    // ---- Link drawing ----

    _drawAllLinks(ctx){
        for(const e of this._edges){
            // Cached for the particle loop, which runs every frame; everything that changes a
            // link's visibility also redraws this layer.
            e._vis=false;
            if(e._x1==null)continue;
            // Hide links to filtered-out nodes
            if(e._isWan&&!this._isCloudVisible())continue;
            if(e._isCl){
                const child=e.tn||e.fn;
                if(child&&!this._isNodeVisible(child))continue;
            }
            if(!this._isEdgeVisible(e))continue;
            e._vis=true;
            // A client link carries no label, so off screen it has nothing to draw.
            if(e._isCl&&!this._edgeInView(e))continue;
            const off=this._isOffline(e.lk.fromNodeId)||this._isOffline(e.lk.toNodeId);
            const r=off?null:(this._liveRates[e.lk.portKey]||this._liveRates[e.lk.id]);
            const dn=r?.downstreamBps??0,up=r?.upstreamBps??0;
            const cap=e.lk.capacityBps||1e9;
            // Full-duplex: reserve the top (red) color for BOTH directions
            // saturated. Busy direction drives the ramp; the quiet one must also
            // load up to reach full - a lone saturated direction tops out amber.
            const dU=Math.min(dn/cap,1),uU=Math.min(up/cap,1);
            const u=0.75*Math.max(dU,uU)+0.25*Math.min(dU,uU);
            const band=edgeBand(e);
            ctx.strokeStyle=pipeClr(Math.min(u,1),band);
            ctx.lineWidth=pipeW(e.lk.capacityBps);
            ctx.lineCap='round';
            ctx.globalAlpha=e._isCl?0.3+u*0.45:0.5+u*0.5;
            // A WAN tunneled over the LAN (GRE from a cellular modem) is dashed: it crosses the LAN.
            const tunnel=e._isWan&&e.lk.viaTunnel&&e.lk.viaNodeId&&this._treeMap.get(e.lk.viaNodeId)?.x!=null;
            if(tunnel)ctx.setLineDash([6,5]);
            strokeOrtho(ctx,e._x1,e._y1,e._x2,e._y2,e._midYOff,e._hz);
            if(tunnel)ctx.setLineDash([]);

            // Capacity / speed label on infra and WAN links
            if(!e._isCl){
                ctx.globalAlpha=1;
                const isWan=e._isWan;
                // WAN: on the cloud's leg, before the crossbar. Infra: on the child's leg, after it.
                const lp=this._elbowLabel(e,isWan,isWan?-28:20);
                const mx=lp.x,my=lp.y;
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
                if(txt&&!off&&!this._hideRates) this._pendingLinkLabels.push({mx,my,txt,txtColor,txtItalic,e});
            }
        }
        ctx.globalAlpha=1;
    }

    _drawLinkSpeedLabels(ctx){
        const normalFont=`${G.rateFont}px ${FONT}`;
        const italicFont=`italic ${G.rateFont}px ${FONT}`;
        ctx.font=normalFont;
        this._linkLabelRects=[];
        for(const{mx:mx0,my:my0,txt,txtColor,txtItalic,e}of(this._pendingLinkLabels||[])){
            ctx.font=txtItalic?italicFont:normalFont;
            const tw=ctx.measureText(txt).width+12;
            const at=this._slideAlong(e,{x:mx0,y:my0},tw,16);
            if(!at)continue;
            const{x:mx,y:my}=at;
            ctx.fillStyle=C.labelBg;
            ctx.globalAlpha=1;
            this._roundRect(ctx,mx-tw/2,my-8,tw,16,4);
            ctx.fill();
            const r={x:mx-tw/2,y:my-8,w:tw,h:16,owner:e};
            this._linkLabelRects.push(r);
            this._labelTaken?.push(r);
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

            // The globe is the icon here, so it grows on hover; its labels below do not.
            const logo=this._clientIcon(cloud.d.logoUrl);
            this._scaledAt(ctx,cx,cy,this._hoverScale(cloud,HOVER_GROW.cloud),()=>{
            // The ISP's logo replaces the globe: its visible mark as tall as the globe, bottom on the
            // globe's bottom, so a wide logo's padding never opens a gap above the name. A square
            // logo's corners overhang the circle on purpose. Greyed like the globe when unresolved.
            if(logo){
                const e=this._clientIcons.get(cloud.d.logoUrl);
                const box=e.box??=opaqueBounds(logo);
                const {w,h}=logoFit(box,r*2);
                ctx.globalAlpha=greyed?0.4:1;
                ctx.drawImage(logo,box.x,box.y,box.w,box.h,cx-w/2,cy+r-h,w,h);
                return;
            }
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
            });

            ctx.globalAlpha=1;

            // A cellular WAN's badge, at the globe's top right: "5G" or "LTE".
            // The modem's mode at the scrub instant in playback; live, the snapshot's.
            const cellKind=flowData.getClientStats()?.[cloud.d.cellularNodeId]?.cellularMode??cloud.d.cellularKind;
            if(cellKind){
                const txt=String(cellKind);
                ctx.font=`600 9px ${FONT}`;
                const bw=ctx.measureText(txt).width+8, bh=13;
                const bx=cx+r*0.62, by=cy-r*0.62-bh/2;
                ctx.fillStyle=C.labelBg;
                this._roundRect(ctx,bx-bw/2,by,bw,bh,6.5); ctx.fill();
                ctx.strokeStyle=greyed?'#3a4455':C.globeStroke; ctx.lineWidth=1;
                this._roundRect(ctx,bx-bw/2,by,bw,bh,6.5); ctx.stroke();
                ctx.fillStyle=greyed?C.textMuted:C.text;
                ctx.textAlign='center'; ctx.textBaseline='middle';
                ctx.fillText(txt,bx,by+bh/2+0.5);
            }

            // Name
            const name=demoMask(cloud.d.name||cloud.d.asnName||'WAN');
            ctx.fillStyle=greyed?C.textMuted:C.textSec;
            ctx.font=`500 ${G.nameFont}px ${FONT}`;
            ctx.textAlign='center'; ctx.textBaseline='middle';
            ctx.fillText(name,cx,cy+r+8+G.nameFont/2);

            // RTT badge
            let rttTxt='';
            if(cloud.d.rttAvgMs!=null)rttTxt+=`${cloud.d.rttAvgMs.toFixed(1)} ms`;
            if(cloud.d.lossPercent&&cloud.d.lossPercent>0)rttTxt+=(rttTxt?' / ':'')+`${cloud.d.lossPercent.toFixed(1)}% loss`;
            if(rttTxt){
                ctx.fillStyle=C.textMuted;
                ctx.font=`${G.rateFont-1}px ${FONT}`;
                ctx.fillText(rttTxt,cx,cy+r+22+(G.rateFont-1)/2);
            }
        }
    }

    // ---- Node drawing ----

    _drawAllNodes(ctx,n){
        // VirtualHub: show as compact label with member count, skip children
        if(n.d.kind===NK.VirtualHub){
            if(!this._hideVirtualHubs)this._drawFading(ctx,n,c=>this._drawHubNode(c,n));
            return;
        }
        this._drawFading(ctx,n,c=>this._drawInfraNode(c,n));
        for(const c of n.infra)this._drawAllNodes(ctx,c);
        const shown=Math.min(n.clients.length,G.maxClients);
        for(let i=0;i<shown;i++){
            const c=n.clients[i];
            if(!this._isNodeVisible(c))continue;
            // Off screen: the margin covers the name label, the widest thing a client draws.
            if(this._inView(c.x,c.y,CLIENT_CULL_X,CLIENT_CULL_Y))this._drawFading(ctx,c,x=>this._drawClientNode(x,c));
            else c._nameRect=null;
        }
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
        let labelR=r;

        // The most specific icon among its members, chosen by the server; else the plain ring.
        const icon=this._clientIcon(n.d.iconUrl);
        if(icon){
            const {w,h}=this._iconSize(icon,26);
            this._drawIconRing(ctx,x,y,r,color);
            this._scaledAt(ctx,x,y,this._hoverScale(n,HOVER_GROW.hub),()=>ctx.drawImage(icon,x-w/2,y-h/2,w,h));
            labelR=Math.max(r,h/2);
        } else {
            ctx.strokeStyle=color; ctx.lineWidth=2; ctx.globalAlpha=0.6;
            ctx.beginPath(); ctx.arc(x,y,r,0,Math.PI*2); ctx.stroke();
            ctx.fillStyle=withAlpha(color,0.1);
            ctx.beginPath(); ctx.arc(x,y,r-1,0,Math.PI*2); ctx.fill();
            ctx.globalAlpha=1;
        }

        // Label - name may already include count from the server
        const name=demoMask(n.d.name||'Hub');
        const hasCount=/\(\d+\)/.test(name);
        const label=hasCount?name:memberCount>0?`${name} (${memberCount})`:name;
        const dn=label.length>28?label.slice(0,27)+'…':label;
        ctx.fillStyle=C.textSec;
        ctx.font=`${G.nameFont}px ${FONT}`;
        ctx.textAlign='center'; ctx.textBaseline='middle';
        ctx.fillText(dn,x,y+labelR+4+G.nameFont/2);
    }

    _drawInfraNode(ctx,n){
        const x=n.x, y=n.y;
        const ov=this._nodeOverlays?.[n.d.id];
        const color=ov?.color||nodeClr(n.d.kind);
        const hw=G.boxW/2, hh=G.boxH/2;
        // Prefer the live/historic badge online state over the snapshot's build-time
        // value so the dimming tracks the timeline and live changes between rebuilds.
        const badge=flowData.getNodeBadges()?.[n.d.id];
        const online=badge?badge.online!==false:n.d.online;
        let op=online?1:0.35;
        if(ov?.dim)op=Math.min(op,0.35);

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
        this._scaledAt(ctx,x,y,this._hoverScale(n,HOVER_GROW.device),()=>{
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
        });
        // A wireless uplink (mesh AP, Device Bridge) or a monitored cellular modem: its signal down the
        // card's right edge. Playback stats first, bars and color from the same source.
        const sig=flowData.getClientStats()?.[n.d.id]?.signalBars!=null?flowData.getClientStats()[n.d.id]:n.d;
        if(sig.signalBars!=null)this._drawUplinkMeter(ctx,x+hw-3.5,y,sig.signalColor,sig.signalBars);
        ctx.globalAlpha=1;

        // Name label
        const name=demoMask(n.d.name||n.d.model||'');
        n._nameRect=null;
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
            n._nameRect={x:x-tw/2,y:ly-8,w:tw,h:16};
        }

        // Overlay badge: small filled disc at the card's top-right corner
        if(ov?.badge){
            const bx=x+hw-2, by=y-hh+2, br=9;
            ctx.fillStyle=ov.color||C.textSec;
            ctx.beginPath(); ctx.arc(bx,by,br,0,Math.PI*2); ctx.fill();
            ctx.strokeStyle=C.bg; ctx.lineWidth=1.5;
            ctx.beginPath(); ctx.arc(bx,by,br,0,Math.PI*2); ctx.stroke();
            ctx.fillStyle='#fff';
            ctx.font=`600 10px ${FONT}`;
            ctx.textAlign='center'; ctx.textBaseline='middle';
            ctx.fillText(String(ov.badge).slice(0,2),bx,by+1);
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
        // A layout transition slides the client to its new AP instead.
        if(n._roamFadeUntil&&this._trans)n._roamFadeUntil=null;
        if(n._roamFadeUntil){
            const t=1-Math.max(0,(n._roamFadeUntil-performance.now())/ROAM_FADE_MS);
            if(t>=1)n._roamFadeUntil=null; else op*=t;
        }

        ctx.globalAlpha=op;
        const icon=this._clientIcon(n.d.iconUrl);
        const hs=this._hoverScale(n,HOVER_GROW.client);
        let labelR=r;
        if(icon){
            // The client's UniFi Network icon over a disc ringed in the color the plain dot would be.
            // The icon is larger than the ring on purpose, and keeps its own aspect ratio.
            const R=r+3;
            const {w,h}=this._iconSize(icon,26);
            this._drawIconRing(ctx,x,y,R,color);
            ctx.globalAlpha=online?Math.min(1,op+0.3):op;
            this._scaledAt(ctx,x,y,hs,()=>ctx.drawImage(icon,x-w/2,y-h/2,w,h));
            ctx.globalAlpha=op;
            labelR=Math.max(R,h/2);
            if(n.d.kind===NK.WifiClient){
                const sg=flowData.getClientStats()?.[n.d.id];
                const sBars=sg?.signalBars??n.d.signalBars, sColor=sg?.signalColor??n.d.signalColor;
                if(sBars!=null) this._drawSignalBars(ctx,x+Math.max(R,w/2)+2.5,y,sColor,sBars);
            }
        } else if(n.d.kind===NK.WifiClient){
            this._scaledAt(ctx,x,y,hs,()=>{
                ctx.fillStyle=color;
                ctx.beginPath(); ctx.arc(x,y,r,0,Math.PI*2); ctx.fill();
                ctx.strokeStyle='rgba(255,255,255,0.5)'; ctx.lineWidth=0.8;
                ctx.beginPath();
                ctx.moveTo(x-2.5,y-0.5);
                ctx.quadraticCurveTo(x,y-4,x+2.5,y-0.5);
                ctx.stroke();
            });
            // Live stats first, as the band lookup already does: the label reads that value, so
            // taking the class off the node colors the bars after a different, older reading.
            const sg=flowData.getClientStats()?.[n.d.id];
            const sBars=sg?.signalBars??n.d.signalBars, sColor=sg?.signalColor??n.d.signalColor;
            if(sBars!=null) this._drawSignalBars(ctx,x+r+2.5,y,sColor,sBars);
        } else {
            this._scaledAt(ctx,x,y,hs,()=>{
                const s=r*0.9;
                ctx.fillStyle=color;
                this._roundRect(ctx,x-s,y-s+0.5,s*2,s*1.5,1.5);
                ctx.fill();
                ctx.beginPath(); ctx.moveTo(x,y+s*0.5+0.5); ctx.lineTo(x,y+s+1);
                ctx.strokeStyle=color; ctx.lineWidth=1.2; ctx.stroke();
            });
        }
        ctx.globalAlpha=1;
        n._labelR=labelR;

        // Name label
        const name=demoMask(n.d.name||n.d.ip||'');
        n._nameRect=null;
        if(name){
            const dn=name.length>32?name.slice(0,31)+'…':name;
            ctx.fillStyle=C.textMuted;
            ctx.font=`${G.clientFont}px ${FONT}`;
            // 'middle', not 'top': Safari puts a 'top' line a few px lower than Chrome does.
            ctx.textAlign='center'; ctx.textBaseline='middle';
            ctx.fillText(dn,x,y+labelR+3+G.clientFont/2);
            const tw=ctx.measureText(dn).width;
            n._nameRect={x:x-tw/2,y:y+labelR+3,w:tw,h:G.clientFont+2};
        }
    }

    // The disc behind an icon and its ring: full strength over the top, fading from 10 and 2
    // o'clock (a quarter of the way down), already well faded at 3 and 9, faint at the bottom.
    _drawIconRing(ctx,x,y,R,color){
        ctx.fillStyle=C.bg;
        ctx.beginPath(); ctx.arc(x,y,R,0,Math.PI*2); ctx.fill();
        const ring=ctx.createLinearGradient(x,y-R,x,y+R);
        ring.addColorStop(0,color); ring.addColorStop(0.25,color);
        ring.addColorStop(0.5,withAlpha(color,0.4)); ring.addColorStop(1,withAlpha(color,0.1));
        ctx.strokeStyle=ring; ctx.lineWidth=1.4;
        ctx.beginPath(); ctx.arc(x,y,R,0,Math.PI*2); ctx.stroke();
    }

    // An icon box scaled to fit, keeping the image's aspect ratio.
    _iconSize(icon,box){
        const k=box/Math.max(icon.naturalWidth||1,icon.naturalHeight||1);
        return {w:(icon.naturalWidth||box)*k,h:(icon.naturalHeight||box)*k};
    }

    // A client icon once loaded, else null (the plain dot draws meanwhile).
    _clientIcon(url){
        if(!url)return null;
        const e=this._clientIcons.get(url)||this._loadClientIcon(url);
        return e.ok?e.img:null;
    }

    // One Image per URL. Clients draw on the static layer, so a late load must ask for a redraw.
    _loadClientIcon(url){
        const e={img:new Image(),ok:false};
        e.promise=new Promise(resolve=>{
            e.img.onload=()=>{e.ok=true;this._needsStaticRedraw=true;resolve();};
            e.img.onerror=()=>{console.debug('[lan-flow-map-2d] client icon failed to load',url);resolve();};
        });
        e.img.src=url;
        this._clientIcons.set(url,e);
        return e;
    }

    // Five bars rising left to right, the Client Performance hero at map scale. Lit count and
    // color both arrive per node; nothing about the curve is decided here.
    _drawSignalBars(ctx,x,y,color,lit){
        const w=1.6, gap=1, h=9.45, base=y+4.55, on=color||C.textMuted;
        for(let i=0;i<5;i++){
            const bh=h*(0.2+i*0.2);
            ctx.fillStyle=i<lit?on:'rgba(255,255,255,0.10)';
            ctx.fillRect(x+i*(w+gap), base-bh, w, bh);
        }
    }

    // The client bars turned upright for a device card: five segments rising bottom to top, each
    // wider than the one below and right-aligned at `right`, in the gap beside the device icon.
    _drawUplinkMeter(ctx,right,y,color,lit){
        const h=6.3, gap=1.4, top=y-(5*h+4*gap)/2, on=color||C.textMuted;
        for(let i=0;i<5;i++){
            const w=2.5+i*0.5, sy=top+(4-i)*(h+gap);
            ctx.fillStyle=i<lit?on:'rgba(255,255,255,0.10)';
            this._roundRect(ctx,right-w,sy,w,h,0.8);
            ctx.fill();
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
            n._rateRect=null;
            if(n._rateY&&!(n._tAlpha<1)){
                let downBps=0,upBps=0,any=false;
                const b=badges?.[n.d.id];
                const hasFab=b&&(b.fabricIngressBps!=null||b.fabricEgressBps!=null);
                const hasAgg=b&&(b.aggregateInBps!=null||b.aggregateOutBps!=null);

                if(hasFab){downBps=b.fabricIngressBps||0;upBps=b.fabricEgressBps||0;any=downBps>0||upBps>0;}
                else if(hasAgg){
                    if(n.d.kind===NK.AP){downBps=b.aggregateOutBps||0;upBps=b.aggregateInBps||0;}
                    else{downBps=b.aggregateInBps||0;upBps=b.aggregateOutBps||0;}
                    any=downBps>0||upBps>0;
                }else if(b?.online!==false){
                    // No figures in the badge: sum this device's own links, which is what the
                    // tooltip and the 3D map do. A UniFi Device Bridge is deliberately left out of
                    // the fabric sum, so without this it showed a rate on hover and nothing at all
                    // under the device. Skipped for a device reported offline: it has no figures
                    // either, and its last link rates linger in the live cache.
                    for(const e of this._edges){
                        if(e.lk.fromNodeId!==n.d.id&&e.lk.toNodeId!==n.d.id)continue;
                        const r=this._liveRates[e.lk.portKey]||this._liveRates[e.lk.id];
                        if(!r)continue;
                        any=true;
                        // Same orientation the tooltip uses: a rate is stated from the far end of
                        // the link, so it swaps on links where this node is the source. Summing
                        // raw put the bridge's numbers the wrong way round against its own tooltip.
                        const dl=r.downstreamBps||0,ul=r.upstreamBps||0;
                        if(e.lk.toNodeId===n.d.id){downBps+=dl;upBps+=ul;}
                        else{downBps+=ul;upBps+=dl;}
                    }
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
                    n._rateRect={x:n.x-tw/2,y:n._rateY-8,w:tw,h:16};
                    this._labelTaken?.push(n._rateRect);
                }
            }
            for(const c of n.infra)drawNodeRate(c);
        };
        if(this._root)drawNodeRate(this._root);

        // Device rates and names are in _labelTaken now; client rates join them below, then each
        // link rate slides clear of all of it (_slideAlong).
        const hz=this._hz;

        // Client rate labels (same style as node/link labels)
        ctx.font=`${G.rateFont}px ${FONT}`;
        for(const e of this._edges){
            if(e._x1==null||!e._isCl)continue;
            const child=e.tn||e.fn;
            if(!child||!this._isNodeVisible(child)||child._tAlpha<1)continue;
            const r=this._liveRates[e.lk.portKey]||this._liveRates[e.lk.id];
            if(!r)continue;
            const dn=r.downstreamBps??0,up=r.upstreamBps??0;
            if(dn>THRESH||up>THRESH){
                const cx=child.x;
                // Below the name, which an icon pushes further down than the plain dot.
                const cy=child.y+(child._labelR??G.clientR)+24;
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
                this._labelTaken?.push({x:cx-tw/2,y:cy-8,w:tw,h:16});
            }
        }

        // Link rate labels. Top-down they sit on the child's leg, which is long enough for the
        // pair side by side. Left to right the crossbar is the only run, a tier gap from the
        // boxes either side and shared with siblings, so the pair stacks. Either way it slides
        // along its run to the nearest spot clear of everything already placed.
        for(const e of this._edges){
            if(e._x1==null||e._isCl||e._isWan)continue;
            const r=this._liveRates[e.lk.portKey]||this._liveRates[e.lk.id];
            if(!r)continue;
            const dn=r.downstreamBps??0,up=r.upstreamBps??0;
            if(!(dn>THRESH||up>THRESH))continue;
            const lp=this._rateLabel(e);
            const dTxt='↓'+(dn>0?formatBps(dn):'0 bps');
            const uTxt='↑'+(up>0?formatBps(up):'0 bps');
            if(!hz){
                const tw=ctx.measureText(dTxt+' '+uTxt).width+14;
                const at=this._slideAlong(e,lp,tw,16);
                if(!at)continue;
                const{x:mx,y:my}=at;
                this._labelTaken?.push({x:mx-tw/2,y:my-8,w:tw,h:16,owner:e});
                ctx.fillStyle=C.labelBg;
                this._roundRect(ctx,mx-tw/2,my-8,tw,16,4);
                ctx.fill();
                ctx.textBaseline='middle';
                ctx.textAlign='right'; ctx.fillStyle=C.downstream;
                ctx.fillText(dTxt,mx-4,my);
                ctx.textAlign='left'; ctx.fillStyle=C.upstream;
                ctx.fillText(uTxt,mx+4,my);
                continue;
            }
            const w=Math.max(ctx.measureText(dTxt).width,ctx.measureText(uTxt).width)+12, h=30;
            const at=this._slideAlong(e,lp,w,h);
            if(!at)continue;
            const{x:mx,y:my}=at;
            this._labelTaken?.push({x:mx-w/2,y:my-h/2,w,h,owner:e});
            ctx.fillStyle=C.labelBg;
            this._roundRect(ctx,mx-w/2,my-h/2,w,h,4);
            ctx.fill();
            ctx.textBaseline='middle'; ctx.textAlign='center';
            ctx.fillStyle=C.downstream; ctx.fillText(dTxt,mx,my-7);
            ctx.fillStyle=C.upstream; ctx.fillText(uTxt,mx,my+7);
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
        if(!this._onScreen||document.hidden){this._animId=requestAnimationFrame(()=>this._animate());return;}

        if(this._liveOnly||!flowData.isPaused()){
            for(const s of this._streams)s.advance(dt);
        }
        if(this._trans)this._stepTransition(now);
        this._stepHover(dt);
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
    if(opts?.focusClient)_inst._focus={mac:String(opts.focusClient).toLowerCase().replaceAll('-',':'),until:null,giveUp:performance.now()+FOCUS_GIVE_UP_MS};
    // Fullscreen the element that holds the map AND its sibling controls, where a page has them:
    // expanding the stage alone leaves its timeline behind the overlay.
    if(opts?.fullscreenEl)_inst._fsTarget=document.getElementById(opts.fullscreenEl);
    if(opts?.hideOverlayControls)_inst._hideOverlayControls=true;
    if(opts?.hideFilter)_inst._hideFilter=true;
    if(opts?.hideVirtualHubs)_inst._hideVirtualHubs=true;
    if(opts?.hideClouds)_inst._hideClouds=true;
    if(opts?.hideWiredClients)_inst._hideWiredClients=true;
    if(opts?.hideWifiClients)_inst._hideWifiClients=true;
    if(opts?.hideHelp)_inst._hideHelp=true;
    if(opts?.hideRates)_inst._hideRates=true;
    if(opts?.hideScrubber)_inst._hideScrubber=true;
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

// Per-node visual overlays (Firmware Rollout): map of node id OR raw device MAC to
// {color,badge,pulse,dim}. Keys are normalized to the map's "dev-<mac>" ids. Passing
// null/empty clears. Default rendering is untouched when no overlay is set.
export function setNodeOverlays(map){
    if(!_inst)return;
    let normalized=null;
    if(map){
        normalized={};
        for(const k in map){
            const key=k.startsWith('dev-')?k:'dev-'+k.toLowerCase().replaceAll('-',':');
            normalized[key]=map[k];
        }
    }
    _inst.setOverlays(normalized);
}

export function clearNodeOverlays(){
    if(_inst)_inst.setOverlays(null);
}

// Re-read rates from the store and re-drive the particle streams. A host that publishes its
// own frames (Firmware Rollout's historic playback) calls this instead of relying on the
// subscription, whose listener errors the store swallows. Rates are replaced, not merged, so
// a link that is idle at this instant stops rather than streaming its last sample forever.
export function refreshRates(){
    if(!_inst)return;
    _inst._liveRates={...flowData.getLiveRates()};
    _inst._updateStreamRates();
    _inst._updateCloudStats();
    _inst._needsStaticRedraw=true;
}
