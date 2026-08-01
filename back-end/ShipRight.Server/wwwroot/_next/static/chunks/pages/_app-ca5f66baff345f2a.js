(self.webpackChunk_N_E=self.webpackChunk_N_E||[]).push([[888],{6840:function(e,t,r){(window.__NEXT_P=window.__NEXT_P||[]).push(["/_app",function(){return r(5545)}])},5545:function(e,t,r){"use strict";r.r(t),r.d(t,{default:function(){return d}});var a=r(5893),i=r(8409),n=r.n(i),o=r(7294),s=r(6501),l=r(7857),c=r(9510),u=r(1493);function d(e){let{Component:t,pageProps:r}=e,i=(0,c.t)(e=>e.accessToken);return(0,o.useEffect)(()=>{(0,u.M8)(i)},[i]),(0,a.jsx)(l.f,{children:(0,a.jsxs)("div",{className:n().className,children:[(0,a.jsx)(t,{...r}),(0,a.jsx)(s.x7,{position:"bottom-right"})]})})}r(876)},1493:function(e,t,r){"use strict";r.d(t,{CD:function(){return s},M8:function(){return i},hi:function(){return o}});let a=null;function i(e){a=e}async function n(e,t,r){let i={};a&&(i.Authorization="Bearer ".concat(a)),r&&(i["Content-Type"]="application/json");let n=await fetch("".concat("").concat(t),{method:e,headers:i,body:r?JSON.stringify(r):void 0});if(204===n.status)return;let o=await n.json().catch(()=>({isError:!0,message:"HTTP ".concat(n.status)}));if(!n.ok)throw o;return o}let o={get:e=>n("GET",e),post:(e,t)=>n("POST",e,t),put:(e,t)=>n("PUT",e,t),delete:e=>n("DELETE",e),getRaw:async function(e){let t={};a&&(t.Authorization="Bearer ".concat(a));let r=await fetch("".concat("").concat(e),{headers:t});if(!r.ok)throw await r.json().catch(()=>({message:"HTTP ".concat(r.status)}));return r.text()}};function s(e){return"".concat("").concat(e)}},9510:function(e,t,r){"use strict";let a,i,n;r.d(t,{i:function(){return m},t:function(){return f}});var o=r(7294);let s=e=>{let t;let r=new Set,a=(e,a)=>{let i="function"==typeof e?e(t):e;if(!Object.is(i,t)){let e=t;t=(null!=a?a:"object"!=typeof i||null===i)?i:Object.assign({},t,i),r.forEach(r=>r(t,e))}},i=()=>t,n={setState:a,getState:i,getInitialState:()=>o,subscribe:e=>(r.add(e),()=>r.delete(e))},o=t=e(a,i,n);return n},l=e=>e?s(e):s,c=e=>e,u=e=>{let t=l(e),r=e=>(function(e,t=c){let r=o.useSyncExternalStore(e.subscribe,o.useCallback(()=>t(e.getState()),[e,t]),o.useCallback(()=>t(e.getInitialState()),[e,t]));return o.useDebugValue(r),r})(t,e);return Object.assign(r,t),r},d=e=>t=>{try{let r=e(t);if(r instanceof Promise)return r;return{then:e=>d(e)(r),catch(e){return this}}}catch(e){return{then(e){return this},catch:t=>d(t)(e)}}},f=(a?u(a):u)((i=e=>({accessToken:null,refreshToken:null,user:null,isAuthenticated:!1,setAuth:(t,r,a)=>{e({accessToken:t,refreshToken:r,user:a,isAuthenticated:!0})},clearAuth:()=>{e({accessToken:null,refreshToken:null,user:null,isAuthenticated:!1})}}),n={name:"shipright-auth",partialize:e=>({accessToken:e.accessToken,refreshToken:e.refreshToken,user:e.user,isAuthenticated:e.isAuthenticated})},(e,t,r)=>{let a,o={storage:function(e,t){let r;try{r=e()}catch(e){return}return{getItem:e=>{var t;let a=e=>null===e?null:JSON.parse(e,void 0),i=null!=(t=r.getItem(e))?t:null;return i instanceof Promise?i.then(a):a(i)},setItem:(e,t)=>r.setItem(e,JSON.stringify(t,void 0)),removeItem:e=>r.removeItem(e)}}(()=>window.localStorage),partialize:e=>e,version:0,merge:(e,t)=>({...t,...e}),...n},s=!1,l=0,c=new Set,u=new Set,f=o.storage;if(!f)return i((...t)=>{console.warn(`[zustand persist middleware] Unable to update item '${o.name}', the given storage is currently unavailable.`),e(...t)},t,r);let p=()=>{let e=o.partialize({...t()});return f.setItem(o.name,{state:e,version:o.version})},m=r.setState;r.setState=(e,t)=>(m(e,t),p());let h=i((...t)=>(e(...t),p()),t,r);r.getInitialState=()=>h;let g=()=>{var r,i;if(!f)return;let n=++l;s=!1,c.forEach(e=>{var r;return e(null!=(r=t())?r:h)});let m=(null==(i=o.onRehydrateStorage)?void 0:i.call(o,null!=(r=t())?r:h))||void 0;return d(f.getItem.bind(f))(o.name).then(e=>{if(e){if("number"!=typeof e.version||e.version===o.version)return[!1,e.state];if(o.migrate){let t=o.migrate(e.state,e.version);return t instanceof Promise?t.then(e=>[!0,e]):[!0,t]}console.error("State loaded from storage couldn't be migrated since no migrate function was provided")}return[!1,void 0]}).then(r=>{var i;if(n!==l)return;let[s,c]=r;if(e(a=o.merge(c,null!=(i=t())?i:h),!0),s)return p()}).then(()=>{n===l&&(null==m||m(t(),void 0),a=t(),s=!0,u.forEach(e=>e(a)))}).catch(e=>{n===l&&(null==m||m(void 0,e))})};return r.persist={setOptions:e=>{o={...o,...e},e.storage&&(f=e.storage)},clearStorage:()=>{null==f||f.removeItem(o.name)},getOptions:()=>o,rehydrate:()=>g(),hasHydrated:()=>s,onHydrate:e=>(c.add(e),()=>{c.delete(e)}),onFinishHydration:e=>(u.add(e),()=>{u.delete(e)})},o.skipHydration||g(),a||h}));async function p(e,t,r,a){let i={};r&&(i["Content-Type"]="application/json"),a&&(i["X-Refresh-Token"]=a);let n=await fetch("".concat("").concat(t),{method:e,headers:Object.keys(i).length>0?i:void 0,body:r?JSON.stringify(r):void 0}),o=await n.json();if(!n.ok)throw o;return o}let m={login:(e,t)=>p("POST","/api/auth/login",{email:e,password:t}),signup:(e,t,r,a)=>p("POST","/api/auth/signup",{email:e,password:t,name:r,companyName:a}),refresh:e=>p("POST","/api/auth/refresh",void 0,e),forgotPassword:e=>p("POST","/api/auth/forgot-password",{email:e}),resetPassword:(e,t)=>p("POST","/api/auth/reset-password",{token:e,newPassword:t})}},7857:function(e,t,r){"use strict";r.d(t,{F:function(){return l},f:function(){return s}});var a=r(5893),i=r(7294);let n=(0,i.createContext)({theme:"system",setTheme:()=>{}}),o="shipright-theme";function s(e){let{children:t}=e,[r,s]=(0,i.useState)("system"),l=(0,i.useCallback)(e=>{let t=document.documentElement;"system"===e?t.removeAttribute("data-theme"):t.setAttribute("data-theme",e),localStorage.setItem(o,e),s(e)},[]);return(0,i.useEffect)(()=>{let e=localStorage.getItem(o);("dark"===e||"light"===e||"system"===e)&&l(e)},[l]),(0,a.jsx)(n.Provider,{value:{theme:r,setTheme:l},children:t})}function l(){return(0,i.useContext)(n)}},876:function(){},8409:function(e){e.exports={style:{fontFamily:"'__Inter_f367f3', '__Inter_Fallback_f367f3'",fontStyle:"normal"},className:"__className_f367f3",variable:"__variable_f367f3"}},6501:function(e,t,r){"use strict";let a,i;r.d(t,{x7:function(){return ef},ZP:function(){return ep}});var n,o=r(7294);let s={data:""},l=e=>{if("object"==typeof window){let t=(e?e.querySelector("#_goober"):window._goober)||Object.assign(document.createElement("style"),{innerHTML:" ",id:"_goober"});return t.nonce=window.__nonce__,t.parentNode||(e||document.head).appendChild(t),t.firstChild}return e||s},c=/(?:([\u0080-\uFFFF\w-%@]+) *:? *([^{;]+?);|([^;}{]*?) *{)|(}\s*)/g,u=/\/\*[^]*?\*\/|  +/g,d=/\n+/g,f=(e,t)=>{let r="",a="",i="";for(let n in e){let o=e[n];"@"==n[0]?"i"==n[1]?r=n+" "+o+";":a+="f"==n[1]?f(o,n):n+"{"+f(o,"k"==n[1]?"":t)+"}":"object"==typeof o?a+=f(o,t?t.replace(/([^,])+/g,e=>n.replace(/([^,]*:\S+\([^)]*\))|([^,])+/g,t=>/&/.test(t)?t.replace(/&/g,e):e?e+" "+t:t)):n):null!=o&&(n="-"==n[1]?n:n.replace(/[A-Z]/g,"-$&").toLowerCase(),i+=f.p?f.p(n,o):n+":"+o+";")}return r+(t&&i?t+"{"+i+"}":i)+a},p={},m=e=>{if("object"==typeof e){let t="";for(let r in e)t+=r+m(e[r]);return t}return e},h=(e,t,r,a,i)=>{var n;let o=m(e),s=p[o]||(p[o]=(e=>{let t=0,r=11;for(;t<e.length;)r=101*r+e.charCodeAt(t++)>>>0;return"go"+r})(o));if(!p[s]){let t=o!==e?e:(e=>{let t,r,a=[{}];for(;t=c.exec(e.replace(u,""));)t[4]?a.shift():t[3]?(r=t[3].replace(d," ").trim(),a.unshift(a[0][r]=a[0][r]||{})):a[0][t[1]]=t[2].replace(d," ").trim();return a[0]})(e);p[s]=f(i?{["@keyframes "+s]:t}:t,r?"":"."+s)}let l=r&&p.g;return r&&(p.g=p[s]),n=p[s],l?t.data=t.data.replace(l,n):-1===t.data.indexOf(n)&&(t.data=a?n+t.data:t.data+n),s},g=(e,t,r)=>e.reduce((e,a,i)=>{let n=t[i];if(n&&n.call){let e=n(r),t=e&&e.props&&e.props.className||/^go/.test(e)&&e;n=t?"."+t:e&&"object"==typeof e?e.props?"":f(e,""):!1===e?"":e}return e+a+(null==n?"":n)},"");function y(e){let t=this||{},r=e.call?e(t.p):e;return h(r.unshift?r.raw?g(r,[].slice.call(arguments,1),t.p):r.reduce((e,r)=>Object.assign(e,r&&r.call?r(t.p):r),{}):r,l(t.target),t.g,t.o,t.k)}y.bind({g:1});let b,v,x,w=y.bind({k:1});function k(e,t){let r=this||{};return function(){let a=arguments;function i(n,o){let s=Object.assign({},n),l=s.className||i.className;r.p=Object.assign({theme:v&&v()},s),r.o=/go\d/.test(l),s.className=y.apply(r,a)+(l?" "+l:""),t&&(s.ref=o);let c=e;return e[0]&&(c=s.as||e,delete s.as),x&&c[0]&&x(s),b(c,s)}return t?t(i):i}}var E=e=>"function"==typeof e,T=(e,t)=>E(e)?e(t):e,S=(a=0,()=>(++a).toString()),_=()=>{if(void 0===i&&"u">typeof window){let e=matchMedia("(prefers-reduced-motion: reduce)");i=!e||e.matches}return i},O="default",j=(e,t)=>{let{toastLimit:r}=e.settings;switch(t.type){case 0:return{...e,toasts:[t.toast,...e.toasts].slice(0,r)};case 1:return{...e,toasts:e.toasts.map(e=>e.id===t.toast.id?{...e,...t.toast}:e)};case 2:let{toast:a}=t;return j(e,{type:e.toasts.find(e=>e.id===a.id)?1:0,toast:a});case 3:let{toastId:i}=t;return{...e,toasts:e.toasts.map(e=>e.id===i||void 0===i?{...e,dismissed:!0,visible:!1}:e)};case 4:return void 0===t.toastId?{...e,toasts:[]}:{...e,toasts:e.toasts.filter(e=>e.id!==t.toastId)};case 5:return{...e,pausedAt:t.time};case 6:let n=t.time-(e.pausedAt||0);return{...e,pausedAt:void 0,toasts:e.toasts.map(e=>({...e,pauseDuration:e.pauseDuration+n}))}}},I=[],P={toasts:[],pausedAt:void 0,settings:{toastLimit:20}},C={},N=(e,t=O)=>{C[t]=j(C[t]||P,e),I.forEach(([e,r])=>{e===t&&r(C[t])})},A=e=>Object.keys(C).forEach(t=>N(e,t)),$=e=>Object.keys(C).find(t=>C[t].toasts.some(t=>t.id===e)),D=(e=O)=>t=>{N(t,e)},z={blank:4e3,error:4e3,success:2e3,loading:1/0,custom:4e3},H=(e={},t=O)=>{let[r,a]=(0,o.useState)(C[t]||P),i=(0,o.useRef)(C[t]);(0,o.useEffect)(()=>(i.current!==C[t]&&a(C[t]),I.push([t,a]),()=>{let e=I.findIndex(([e])=>e===t);e>-1&&I.splice(e,1)}),[t]);let n=r.toasts.map(t=>{var r,a,i;return{...e,...e[t.type],...t,removeDelay:t.removeDelay||(null==(r=e[t.type])?void 0:r.removeDelay)||(null==e?void 0:e.removeDelay),duration:t.duration||(null==(a=e[t.type])?void 0:a.duration)||(null==e?void 0:e.duration)||z[t.type],style:{...e.style,...null==(i=e[t.type])?void 0:i.style,...t.style}}});return{...r,toasts:n}},F=(e,t="blank",r)=>({createdAt:Date.now(),visible:!0,dismissed:!1,type:t,ariaProps:{role:"status","aria-live":"polite"},message:e,pauseDuration:0,...r,id:(null==r?void 0:r.id)||S()}),M=e=>(t,r)=>{let a=F(t,e,r);return D(a.toasterId||$(a.id))({type:2,toast:a}),a.id},L=(e,t)=>M("blank")(e,t);L.error=M("error"),L.success=M("success"),L.loading=M("loading"),L.custom=M("custom"),L.dismiss=(e,t)=>{let r={type:3,toastId:e};t?D(t)(r):A(r)},L.dismissAll=e=>L.dismiss(void 0,e),L.remove=(e,t)=>{let r={type:4,toastId:e};t?D(t)(r):A(r)},L.removeAll=e=>L.remove(void 0,e),L.promise=(e,t,r)=>{let a=L.loading(t.loading,{...r,...null==r?void 0:r.loading});return"function"==typeof e&&(e=e()),e.then(e=>{let i=t.success?T(t.success,e):void 0;return i?L.success(i,{id:a,...r,...null==r?void 0:r.success}):L.dismiss(a),e}).catch(e=>{let i=t.error?T(t.error,e):void 0;i?L.error(i,{id:a,...r,...null==r?void 0:r.error}):L.dismiss(a)}),e};var R=1e3,J=(e,t="default")=>{let{toasts:r,pausedAt:a}=H(e,t),i=(0,o.useRef)(new Map).current,n=(0,o.useCallback)((e,t=R)=>{if(i.has(e))return;let r=setTimeout(()=>{i.delete(e),s({type:4,toastId:e})},t);i.set(e,r)},[]);(0,o.useEffect)(()=>{if(a)return;let e=Date.now(),i=r.map(r=>{if(r.duration===1/0)return;let a=(r.duration||0)+r.pauseDuration-(e-r.createdAt);if(a<0){r.visible&&L.dismiss(r.id);return}return setTimeout(()=>L.dismiss(r.id,t),a)});return()=>{i.forEach(e=>e&&clearTimeout(e))}},[r,a,t]);let s=(0,o.useCallback)(D(t),[t]),l=(0,o.useCallback)(()=>{s({type:5,time:Date.now()})},[s]),c=(0,o.useCallback)((e,t)=>{s({type:1,toast:{id:e,height:t}})},[s]),u=(0,o.useCallback)(()=>{a&&s({type:6,time:Date.now()})},[a,s]),d=(0,o.useCallback)((e,t)=>{let{reverseOrder:a=!1,gutter:i=8,defaultPosition:n}=t||{},o=r.filter(t=>(t.position||n)===(e.position||n)&&t.height),s=o.findIndex(t=>t.id===e.id),l=o.filter((e,t)=>t<s&&e.visible).length;return o.filter(e=>e.visible).slice(...a?[l+1]:[0,l]).reduce((e,t)=>e+(t.height||0)+i,0)},[r]);return(0,o.useEffect)(()=>{r.forEach(e=>{if(e.dismissed)n(e.id,e.removeDelay);else{let t=i.get(e.id);t&&(clearTimeout(t),i.delete(e.id))}})},[r,n]),{toasts:r,handlers:{updateHeight:c,startPause:l,endPause:u,calculateOffset:d}}},U=w`
from {
  transform: scale(0) rotate(45deg);
	opacity: 0;
}
to {
 transform: scale(1) rotate(45deg);
  opacity: 1;
}`,B=w`
from {
  transform: scale(0);
  opacity: 0;
}
to {
  transform: scale(1);
  opacity: 1;
}`,X=w`
from {
  transform: scale(0) rotate(90deg);
	opacity: 0;
}
to {
  transform: scale(1) rotate(90deg);
	opacity: 1;
}`,Z=k("div")`
  width: 20px;
  opacity: 0;
  height: 20px;
  border-radius: 10px;
  background: ${e=>e.primary||"#ff4b4b"};
  position: relative;
  transform: rotate(45deg);

  animation: ${U} 0.3s cubic-bezier(0.175, 0.885, 0.32, 1.275)
    forwards;
  animation-delay: 100ms;

  &:after,
  &:before {
    content: '';
    animation: ${B} 0.15s ease-out forwards;
    animation-delay: 150ms;
    position: absolute;
    border-radius: 3px;
    opacity: 0;
    background: ${e=>e.secondary||"#fff"};
    bottom: 9px;
    left: 4px;
    height: 2px;
    width: 12px;
  }

  &:before {
    animation: ${X} 0.15s ease-out forwards;
    animation-delay: 180ms;
    transform: rotate(90deg);
  }
`,q=w`
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
`,G=k("div")`
  width: 12px;
  height: 12px;
  box-sizing: border-box;
  border: 2px solid;
  border-radius: 100%;
  border-color: ${e=>e.secondary||"#e0e0e0"};
  border-right-color: ${e=>e.primary||"#616161"};
  animation: ${q} 1s linear infinite;
`,V=w`
from {
  transform: scale(0) rotate(45deg);
	opacity: 0;
}
to {
  transform: scale(1) rotate(45deg);
	opacity: 1;
}`,Y=w`
0% {
	height: 0;
	width: 0;
	opacity: 0;
}
40% {
  height: 0;
	width: 6px;
	opacity: 1;
}
100% {
  opacity: 1;
  height: 10px;
}`,K=k("div")`
  width: 20px;
  opacity: 0;
  height: 20px;
  border-radius: 10px;
  background: ${e=>e.primary||"#61d345"};
  position: relative;
  transform: rotate(45deg);

  animation: ${V} 0.3s cubic-bezier(0.175, 0.885, 0.32, 1.275)
    forwards;
  animation-delay: 100ms;
  &:after {
    content: '';
    box-sizing: border-box;
    animation: ${Y} 0.2s ease-out forwards;
    opacity: 0;
    animation-delay: 200ms;
    position: absolute;
    border-right: 2px solid;
    border-bottom: 2px solid;
    border-color: ${e=>e.secondary||"#fff"};
    bottom: 6px;
    left: 6px;
    height: 10px;
    width: 6px;
  }
`,Q=k("div")`
  position: absolute;
`,W=k("div")`
  position: relative;
  display: flex;
  justify-content: center;
  align-items: center;
  min-width: 20px;
  min-height: 20px;
`,ee=w`
from {
  transform: scale(0.6);
  opacity: 0.4;
}
to {
  transform: scale(1);
  opacity: 1;
}`,et=k("div")`
  position: relative;
  transform: scale(0.6);
  opacity: 0.4;
  min-width: 20px;
  animation: ${ee} 0.3s 0.12s cubic-bezier(0.175, 0.885, 0.32, 1.275)
    forwards;
`,er=({toast:e})=>{let{icon:t,type:r,iconTheme:a}=e;return void 0!==t?"string"==typeof t?o.createElement(et,null,t):t:"blank"===r?null:o.createElement(W,null,o.createElement(G,{...a}),"loading"!==r&&o.createElement(Q,null,"error"===r?o.createElement(Z,{...a}):o.createElement(K,{...a})))},ea=e=>`
0% {transform: translate3d(0,${-200*e}%,0) scale(.6); opacity:.5;}
100% {transform: translate3d(0,0,0) scale(1); opacity:1;}
`,ei=e=>`
0% {transform: translate3d(0,0,-1px) scale(1); opacity:1;}
100% {transform: translate3d(0,${-150*e}%,-1px) scale(.6); opacity:0;}
`,en=k("div")`
  display: flex;
  align-items: center;
  background: #fff;
  color: #363636;
  line-height: 1.3;
  will-change: transform;
  box-shadow: 0 3px 10px rgba(0, 0, 0, 0.1), 0 3px 3px rgba(0, 0, 0, 0.05);
  max-width: 350px;
  pointer-events: auto;
  padding: 8px 10px;
  border-radius: 8px;
`,eo=k("div")`
  display: flex;
  justify-content: center;
  margin: 4px 10px;
  color: inherit;
  flex: 1 1 auto;
  white-space: pre-line;
`,es=(e,t)=>{let r=e.includes("top")?1:-1,[a,i]=_()?["0%{opacity:0;} 100%{opacity:1;}","0%{opacity:1;} 100%{opacity:0;}"]:[ea(r),ei(r)];return{animation:t?`${w(a)} 0.35s cubic-bezier(.21,1.02,.73,1) forwards`:`${w(i)} 0.4s forwards cubic-bezier(.06,.71,.55,1)`}},el=o.memo(({toast:e,position:t,style:r,children:a})=>{let i=e.height?es(e.position||t||"top-center",e.visible):{opacity:0},n=o.createElement(er,{toast:e}),s=o.createElement(eo,{...e.ariaProps},T(e.message,e));return o.createElement(en,{className:e.className,style:{...i,...r,...e.style}},"function"==typeof a?a({icon:n,message:s}):o.createElement(o.Fragment,null,n,s))});n=o.createElement,f.p=void 0,b=n,v=void 0,x=void 0;var ec=({id:e,className:t,style:r,onHeightUpdate:a,children:i})=>{let n=o.useCallback(t=>{if(t){let r=()=>{a(e,t.getBoundingClientRect().height)};r(),new MutationObserver(r).observe(t,{subtree:!0,childList:!0,characterData:!0})}},[e,a]);return o.createElement("div",{ref:n,className:t,style:r},i)},eu=(e,t)=>{let r=e.includes("top"),a=e.includes("center")?{justifyContent:"center"}:e.includes("right")?{justifyContent:"flex-end"}:{};return{left:0,right:0,display:"flex",position:"absolute",transition:_()?void 0:"all 230ms cubic-bezier(.21,1.02,.73,1)",transform:`translateY(${t*(r?1:-1)}px)`,...r?{top:0}:{bottom:0},...a}},ed=y`
  z-index: 9999;
  > * {
    pointer-events: auto;
  }
`,ef=({reverseOrder:e,position:t="top-center",toastOptions:r,gutter:a,children:i,toasterId:n,containerStyle:s,containerClassName:l})=>{let{toasts:c,handlers:u}=J(r,n);return o.createElement("div",{"data-rht-toaster":n||"",style:{position:"fixed",zIndex:9999,top:16,left:16,right:16,bottom:16,pointerEvents:"none",...s},className:l,onMouseEnter:u.startPause,onMouseLeave:u.endPause},c.map(r=>{let n=r.position||t,s=eu(n,u.calculateOffset(r,{reverseOrder:e,gutter:a,defaultPosition:t}));return o.createElement(ec,{id:r.id,key:r.id,onHeightUpdate:u.updateHeight,className:r.visible?ed:"",style:s},"custom"===r.type?T(r.message,r):i?i(r):o.createElement(el,{toast:r,position:n}))}))},ep=L}},function(e){var t=function(t){return e(e.s=t)};e.O(0,[774,179],function(){return t(6840),t(3079)}),_N_E=e.O()}]);