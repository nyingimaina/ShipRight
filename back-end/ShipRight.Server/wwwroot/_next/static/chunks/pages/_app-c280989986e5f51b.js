(self.webpackChunk_N_E=self.webpackChunk_N_E||[]).push([[888],{6840:function(e,t,r){(window.__NEXT_P=window.__NEXT_P||[]).push(["/_app",function(){return r(1506)}])},1506:function(e,t,r){"use strict";r.r(t),r.d(t,{default:function(){return g}});var a=r(5893),n=r(8409),i=r.n(n),s=r(7294),o=r(6501),l=r(7857),c=r(1163),u=r(2735),d=r(4846);let f=["/login","/signup","/forgot-password","/reset-password"];function p(e){let{children:t}=e,r=(0,c.useRouter)(),n=(0,u.t)(e=>e.isAuthenticated),i=(0,d.Jo)(),[o,l]=(0,s.useState)(()=>{var e,t;return null!==(t=null===(e=u.t.persist)||void 0===e?void 0:e.hasHydrated())&&void 0!==t&&t});(0,s.useEffect)(()=>{if(o)return;let e=u.t.persist;if(!e){l(!0);return}return e.onFinishHydration(()=>l(!0))},[o]);let p=f.includes(r.pathname),m="desktop"===i;return((0,s.useEffect)(()=>{!o||!i||m||p||n||r.replace("/login")},[o,i,m,p,n,r]),null!==i&&o&&(m||p||n))?(0,a.jsx)(a.Fragment,{children:t}):null}var m=r(593),h=r(1493);function g(e){let{Component:t,pageProps:r}=e,n=(0,m.t)(e=>e.accessToken);return(0,s.useEffect)(()=>{(0,h.M8)(n)},[n]),(0,a.jsx)(l.f,{children:(0,a.jsxs)("div",{className:i().className,children:[(0,a.jsx)(p,{children:(0,a.jsx)(t,{...r})}),(0,a.jsx)(o.x7,{position:"bottom-right"})]})})}r(876)},1493:function(e,t,r){"use strict";r.d(t,{CD:function(){return f},M8:function(){return s},hi:function(){return d}});var a=r(593);let n=null,i=null;function s(e){n=e}function o(){return i||(i=(async()=>{let e=a.t.getState().refreshToken;if(!e)return null;try{let t=await fetch("".concat("","/api/auth/refresh"),{method:"POST",headers:{"X-Refresh-Token":e}});if(!t.ok)return null;let r=await t.json();return a.t.getState().setTokens(r.accessToken,r.refreshToken),n=r.accessToken,r.accessToken}catch(e){return null}finally{i=null}})()),i}function l(){a.t.getState().clearAuth(),n=null}async function c(e,t,r){let a=arguments.length>3&&void 0!==arguments[3]&&arguments[3],i={};n&&(i.Authorization="Bearer ".concat(n)),r&&(i["Content-Type"]="application/json");let s=await fetch("".concat("").concat(t),{method:e,headers:i,body:r?JSON.stringify(r):void 0});if(401===s.status&&!a){if(await o())return c(e,t,r,!0);l()}if(204===s.status)return;let u=await s.json().catch(()=>({isError:!0,message:"HTTP ".concat(s.status)}));if(!s.ok)throw u;return u}async function u(e){let t=arguments.length>1&&void 0!==arguments[1]&&arguments[1],r={};n&&(r.Authorization="Bearer ".concat(n));let a=await fetch("".concat("").concat(e),{headers:r});if(401===a.status&&!t){if(await o())return u(e,!0);l()}if(!a.ok)throw await a.json().catch(()=>({message:"HTTP ".concat(a.status)}));return a.text()}let d={get:e=>c("GET",e),post:(e,t)=>c("POST",e,t),put:(e,t)=>c("PUT",e,t),delete:e=>c("DELETE",e),getRaw:u};function f(e){return"".concat("").concat(e)}},2735:function(e,t,r){"use strict";let a,n,i;r.d(t,{t:function(){return f}});var s=r(7294);let o=e=>{let t;let r=new Set,a=(e,a)=>{let n="function"==typeof e?e(t):e;if(!Object.is(n,t)){let e=t;t=(null!=a?a:"object"!=typeof n||null===n)?n:Object.assign({},t,n),r.forEach(r=>r(t,e))}},n=()=>t,i={setState:a,getState:n,getInitialState:()=>s,subscribe:e=>(r.add(e),()=>r.delete(e))},s=t=e(a,n,i);return i},l=e=>e?o(e):o,c=e=>e,u=e=>{let t=l(e),r=e=>(function(e,t=c){let r=s.useSyncExternalStore(e.subscribe,s.useCallback(()=>t(e.getState()),[e,t]),s.useCallback(()=>t(e.getInitialState()),[e,t]));return s.useDebugValue(r),r})(t,e);return Object.assign(r,t),r},d=e=>t=>{try{let r=e(t);if(r instanceof Promise)return r;return{then:e=>d(e)(r),catch(e){return this}}}catch(e){return{then(e){return this},catch:t=>d(t)(e)}}},f=(a?u(a):u)((n=e=>({accessToken:null,refreshToken:null,user:null,isAuthenticated:!1,setAuth:(t,r,a)=>{e({accessToken:t,refreshToken:r,user:a,isAuthenticated:!0})},setTokens:(t,r)=>{e({accessToken:t,refreshToken:r,isAuthenticated:!0})},clearAuth:()=>{e({accessToken:null,refreshToken:null,user:null,isAuthenticated:!1})}}),i={name:"shipright-auth",partialize:e=>({accessToken:e.accessToken,refreshToken:e.refreshToken,user:e.user,isAuthenticated:e.isAuthenticated})},(e,t,r)=>{let a,s={storage:function(e,t){let r;try{r=e()}catch(e){return}return{getItem:e=>{var t;let a=e=>null===e?null:JSON.parse(e,void 0),n=null!=(t=r.getItem(e))?t:null;return n instanceof Promise?n.then(a):a(n)},setItem:(e,t)=>r.setItem(e,JSON.stringify(t,void 0)),removeItem:e=>r.removeItem(e)}}(()=>window.localStorage),partialize:e=>e,version:0,merge:(e,t)=>({...t,...e}),...i},o=!1,l=0,c=new Set,u=new Set,f=s.storage;if(!f)return n((...t)=>{console.warn(`[zustand persist middleware] Unable to update item '${s.name}', the given storage is currently unavailable.`),e(...t)},t,r);let p=()=>{let e=s.partialize({...t()});return f.setItem(s.name,{state:e,version:s.version})},m=r.setState;r.setState=(e,t)=>(m(e,t),p());let h=n((...t)=>(e(...t),p()),t,r);r.getInitialState=()=>h;let g=()=>{var r,n;if(!f)return;let i=++l;o=!1,c.forEach(e=>{var r;return e(null!=(r=t())?r:h)});let m=(null==(n=s.onRehydrateStorage)?void 0:n.call(s,null!=(r=t())?r:h))||void 0;return d(f.getItem.bind(f))(s.name).then(e=>{if(e){if("number"!=typeof e.version||e.version===s.version)return[!1,e.state];if(s.migrate){let t=s.migrate(e.state,e.version);return t instanceof Promise?t.then(e=>[!0,e]):[!0,t]}console.error("State loaded from storage couldn't be migrated since no migrate function was provided")}return[!1,void 0]}).then(r=>{var n;if(i!==l)return;let[o,c]=r;if(e(a=s.merge(c,null!=(n=t())?n:h),!0),o)return p()}).then(()=>{i===l&&(null==m||m(t(),void 0),a=t(),o=!0,u.forEach(e=>e(a)))}).catch(e=>{i===l&&(null==m||m(void 0,e))})};return r.persist={setOptions:e=>{s={...s,...e},e.storage&&(f=e.storage)},clearStorage:()=>{null==f||f.removeItem(s.name)},getOptions:()=>s,rehydrate:()=>g(),hasHydrated:()=>o,onHydrate:e=>(c.add(e),()=>{c.delete(e)}),onFinishHydration:e=>(u.add(e),()=>{u.delete(e)})},s.skipHydration||g(),a||h}))},593:function(e,t,r){"use strict";r.d(t,{i:function(){return i},t:function(){return a.t}});var a=r(2735);async function n(e,t,r,a){let n={};r&&(n["Content-Type"]="application/json"),a&&(n["X-Refresh-Token"]=a);let i=await fetch("".concat("").concat(t),{method:e,headers:Object.keys(n).length>0?n:void 0,body:r?JSON.stringify(r):void 0}),s=await i.text(),o=null;if(s)try{o=JSON.parse(s)}catch(e){o=null}if(!i.ok)throw null!=o?o:{message:"Request failed with status ".concat(i.status),isError:!0};return o}let i={login:(e,t)=>n("POST","/api/auth/login",{email:e,password:t}),signup:(e,t,r,a)=>n("POST","/api/auth/signup",{email:e,password:t,name:r,companyName:a}),refresh:e=>n("POST","/api/auth/refresh",void 0,e),forgotPassword:e=>n("POST","/api/auth/forgot-password",{email:e}),resetPassword:(e,t)=>n("POST","/api/auth/reset-password",{token:e,newPassword:t})}},4846:function(e,t,r){"use strict";r.d(t,{Jo:function(){return i}});var a=r(7294);let n=null;function i(){let[e,t]=(0,a.useState)(null);return(0,a.useEffect)(()=>{let e=!0;return(n||(n=fetch("/api/health").then(e=>{if(!e.ok)throw Error("Health check failed");return e.json()}).then(e=>(null==e?void 0:e.mode)==="cloud"?"cloud":"desktop").catch(()=>"desktop")),n).then(r=>{e&&t(r)}),()=>{e=!1}},[]),e}},7857:function(e,t,r){"use strict";r.d(t,{F:function(){return l},f:function(){return o}});var a=r(5893),n=r(7294);let i=(0,n.createContext)({theme:"system",setTheme:()=>{}}),s="shipright-theme";function o(e){let{children:t}=e,[r,o]=(0,n.useState)("system"),l=(0,n.useCallback)(e=>{let t=document.documentElement;"system"===e?t.removeAttribute("data-theme"):t.setAttribute("data-theme",e),localStorage.setItem(s,e),o(e)},[]);return(0,n.useEffect)(()=>{let e=localStorage.getItem(s);("dark"===e||"light"===e||"system"===e)&&l(e)},[l]),(0,a.jsx)(i.Provider,{value:{theme:r,setTheme:l},children:t})}function l(){return(0,n.useContext)(i)}},876:function(){},8409:function(e){e.exports={style:{fontFamily:"'__Inter_f367f3', '__Inter_Fallback_f367f3'",fontStyle:"normal"},className:"__className_f367f3",variable:"__variable_f367f3"}},1163:function(e,t,r){e.exports=r(3079)},6501:function(e,t,r){"use strict";let a,n;r.d(t,{x7:function(){return ef},ZP:function(){return ep}});var i,s=r(7294);let o={data:""},l=e=>{if("object"==typeof window){let t=(e?e.querySelector("#_goober"):window._goober)||Object.assign(document.createElement("style"),{innerHTML:" ",id:"_goober"});return t.nonce=window.__nonce__,t.parentNode||(e||document.head).appendChild(t),t.firstChild}return e||o},c=/(?:([\u0080-\uFFFF\w-%@]+) *:? *([^{;]+?);|([^;}{]*?) *{)|(}\s*)/g,u=/\/\*[^]*?\*\/|  +/g,d=/\n+/g,f=(e,t)=>{let r="",a="",n="";for(let i in e){let s=e[i];"@"==i[0]?"i"==i[1]?r=i+" "+s+";":a+="f"==i[1]?f(s,i):i+"{"+f(s,"k"==i[1]?"":t)+"}":"object"==typeof s?a+=f(s,t?t.replace(/([^,])+/g,e=>i.replace(/([^,]*:\S+\([^)]*\))|([^,])+/g,t=>/&/.test(t)?t.replace(/&/g,e):e?e+" "+t:t)):i):null!=s&&(i="-"==i[1]?i:i.replace(/[A-Z]/g,"-$&").toLowerCase(),n+=f.p?f.p(i,s):i+":"+s+";")}return r+(t&&n?t+"{"+n+"}":n)+a},p={},m=e=>{if("object"==typeof e){let t="";for(let r in e)t+=r+m(e[r]);return t}return e},h=(e,t,r,a,n)=>{var i;let s=m(e),o=p[s]||(p[s]=(e=>{let t=0,r=11;for(;t<e.length;)r=101*r+e.charCodeAt(t++)>>>0;return"go"+r})(s));if(!p[o]){let t=s!==e?e:(e=>{let t,r,a=[{}];for(;t=c.exec(e.replace(u,""));)t[4]?a.shift():t[3]?(r=t[3].replace(d," ").trim(),a.unshift(a[0][r]=a[0][r]||{})):a[0][t[1]]=t[2].replace(d," ").trim();return a[0]})(e);p[o]=f(n?{["@keyframes "+o]:t}:t,r?"":"."+o)}let l=r&&p.g;return r&&(p.g=p[o]),i=p[o],l?t.data=t.data.replace(l,i):-1===t.data.indexOf(i)&&(t.data=a?i+t.data:t.data+i),o},g=(e,t,r)=>e.reduce((e,a,n)=>{let i=t[n];if(i&&i.call){let e=i(r),t=e&&e.props&&e.props.className||/^go/.test(e)&&e;i=t?"."+t:e&&"object"==typeof e?e.props?"":f(e,""):!1===e?"":e}return e+a+(null==i?"":i)},"");function y(e){let t=this||{},r=e.call?e(t.p):e;return h(r.unshift?r.raw?g(r,[].slice.call(arguments,1),t.p):r.reduce((e,r)=>Object.assign(e,r&&r.call?r(t.p):r),{}):r,l(t.target),t.g,t.o,t.k)}y.bind({g:1});let v,b,w,x=y.bind({k:1});function k(e,t){let r=this||{};return function(){let a=arguments;function n(i,s){let o=Object.assign({},i),l=o.className||n.className;r.p=Object.assign({theme:b&&b()},o),r.o=/go\d/.test(l),o.className=y.apply(r,a)+(l?" "+l:""),t&&(o.ref=s);let c=e;return e[0]&&(c=o.as||e,delete o.as),w&&c[0]&&w(o),v(c,o)}return t?t(n):n}}var E=e=>"function"==typeof e,T=(e,t)=>E(e)?e(t):e,S=(a=0,()=>(++a).toString()),O=()=>{if(void 0===n&&"u">typeof window){let e=matchMedia("(prefers-reduced-motion: reduce)");n=!e||e.matches}return n},_="default",j=(e,t)=>{let{toastLimit:r}=e.settings;switch(t.type){case 0:return{...e,toasts:[t.toast,...e.toasts].slice(0,r)};case 1:return{...e,toasts:e.toasts.map(e=>e.id===t.toast.id?{...e,...t.toast}:e)};case 2:let{toast:a}=t;return j(e,{type:e.toasts.find(e=>e.id===a.id)?1:0,toast:a});case 3:let{toastId:n}=t;return{...e,toasts:e.toasts.map(e=>e.id===n||void 0===n?{...e,dismissed:!0,visible:!1}:e)};case 4:return void 0===t.toastId?{...e,toasts:[]}:{...e,toasts:e.toasts.filter(e=>e.id!==t.toastId)};case 5:return{...e,pausedAt:t.time};case 6:let i=t.time-(e.pausedAt||0);return{...e,pausedAt:void 0,toasts:e.toasts.map(e=>({...e,pauseDuration:e.pauseDuration+i}))}}},I=[],P={toasts:[],pausedAt:void 0,settings:{toastLimit:20}},N={},A=(e,t=_)=>{N[t]=j(N[t]||P,e),I.forEach(([e,r])=>{e===t&&r(N[t])})},C=e=>Object.keys(N).forEach(t=>A(e,t)),$=e=>Object.keys(N).find(t=>N[t].toasts.some(t=>t.id===e)),D=(e=_)=>t=>{A(t,e)},z={blank:4e3,error:4e3,success:2e3,loading:1/0,custom:4e3},H=(e={},t=_)=>{let[r,a]=(0,s.useState)(N[t]||P),n=(0,s.useRef)(N[t]);(0,s.useEffect)(()=>(n.current!==N[t]&&a(N[t]),I.push([t,a]),()=>{let e=I.findIndex(([e])=>e===t);e>-1&&I.splice(e,1)}),[t]);let i=r.toasts.map(t=>{var r,a,n;return{...e,...e[t.type],...t,removeDelay:t.removeDelay||(null==(r=e[t.type])?void 0:r.removeDelay)||(null==e?void 0:e.removeDelay),duration:t.duration||(null==(a=e[t.type])?void 0:a.duration)||(null==e?void 0:e.duration)||z[t.type],style:{...e.style,...null==(n=e[t.type])?void 0:n.style,...t.style}}});return{...r,toasts:i}},F=(e,t="blank",r)=>({createdAt:Date.now(),visible:!0,dismissed:!1,type:t,ariaProps:{role:"status","aria-live":"polite"},message:e,pauseDuration:0,...r,id:(null==r?void 0:r.id)||S()}),M=e=>(t,r)=>{let a=F(t,e,r);return D(a.toasterId||$(a.id))({type:2,toast:a}),a.id},R=(e,t)=>M("blank")(e,t);R.error=M("error"),R.success=M("success"),R.loading=M("loading"),R.custom=M("custom"),R.dismiss=(e,t)=>{let r={type:3,toastId:e};t?D(t)(r):C(r)},R.dismissAll=e=>R.dismiss(void 0,e),R.remove=(e,t)=>{let r={type:4,toastId:e};t?D(t)(r):C(r)},R.removeAll=e=>R.remove(void 0,e),R.promise=(e,t,r)=>{let a=R.loading(t.loading,{...r,...null==r?void 0:r.loading});return"function"==typeof e&&(e=e()),e.then(e=>{let n=t.success?T(t.success,e):void 0;return n?R.success(n,{id:a,...r,...null==r?void 0:r.success}):R.dismiss(a),e}).catch(e=>{let n=t.error?T(t.error,e):void 0;n?R.error(n,{id:a,...r,...null==r?void 0:r.error}):R.dismiss(a)}),e};var J=1e3,L=(e,t="default")=>{let{toasts:r,pausedAt:a}=H(e,t),n=(0,s.useRef)(new Map).current,i=(0,s.useCallback)((e,t=J)=>{if(n.has(e))return;let r=setTimeout(()=>{n.delete(e),o({type:4,toastId:e})},t);n.set(e,r)},[]);(0,s.useEffect)(()=>{if(a)return;let e=Date.now(),n=r.map(r=>{if(r.duration===1/0)return;let a=(r.duration||0)+r.pauseDuration-(e-r.createdAt);if(a<0){r.visible&&R.dismiss(r.id);return}return setTimeout(()=>R.dismiss(r.id,t),a)});return()=>{n.forEach(e=>e&&clearTimeout(e))}},[r,a,t]);let o=(0,s.useCallback)(D(t),[t]),l=(0,s.useCallback)(()=>{o({type:5,time:Date.now()})},[o]),c=(0,s.useCallback)((e,t)=>{o({type:1,toast:{id:e,height:t}})},[o]),u=(0,s.useCallback)(()=>{a&&o({type:6,time:Date.now()})},[a,o]),d=(0,s.useCallback)((e,t)=>{let{reverseOrder:a=!1,gutter:n=8,defaultPosition:i}=t||{},s=r.filter(t=>(t.position||i)===(e.position||i)&&t.height),o=s.findIndex(t=>t.id===e.id),l=s.filter((e,t)=>t<o&&e.visible).length;return s.filter(e=>e.visible).slice(...a?[l+1]:[0,l]).reduce((e,t)=>e+(t.height||0)+n,0)},[r]);return(0,s.useEffect)(()=>{r.forEach(e=>{if(e.dismissed)i(e.id,e.removeDelay);else{let t=n.get(e.id);t&&(clearTimeout(t),n.delete(e.id))}})},[r,i]),{toasts:r,handlers:{updateHeight:c,startPause:l,endPause:u,calculateOffset:d}}},U=x`
from {
  transform: scale(0) rotate(45deg);
	opacity: 0;
}
to {
 transform: scale(1) rotate(45deg);
  opacity: 1;
}`,X=x`
from {
  transform: scale(0);
  opacity: 0;
}
to {
  transform: scale(1);
  opacity: 1;
}`,B=x`
from {
  transform: scale(0) rotate(90deg);
	opacity: 0;
}
to {
  transform: scale(1) rotate(90deg);
	opacity: 1;
}`,q=k("div")`
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
    animation: ${X} 0.15s ease-out forwards;
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
    animation: ${B} 0.15s ease-out forwards;
    animation-delay: 180ms;
    transform: rotate(90deg);
  }
`,Z=x`
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
  animation: ${Z} 1s linear infinite;
`,V=x`
from {
  transform: scale(0) rotate(45deg);
	opacity: 0;
}
to {
  transform: scale(1) rotate(45deg);
	opacity: 1;
}`,Y=x`
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
`,ee=x`
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
`,er=({toast:e})=>{let{icon:t,type:r,iconTheme:a}=e;return void 0!==t?"string"==typeof t?s.createElement(et,null,t):t:"blank"===r?null:s.createElement(W,null,s.createElement(G,{...a}),"loading"!==r&&s.createElement(Q,null,"error"===r?s.createElement(q,{...a}):s.createElement(K,{...a})))},ea=e=>`
0% {transform: translate3d(0,${-200*e}%,0) scale(.6); opacity:.5;}
100% {transform: translate3d(0,0,0) scale(1); opacity:1;}
`,en=e=>`
0% {transform: translate3d(0,0,-1px) scale(1); opacity:1;}
100% {transform: translate3d(0,${-150*e}%,-1px) scale(.6); opacity:0;}
`,ei=k("div")`
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
`,es=k("div")`
  display: flex;
  justify-content: center;
  margin: 4px 10px;
  color: inherit;
  flex: 1 1 auto;
  white-space: pre-line;
`,eo=(e,t)=>{let r=e.includes("top")?1:-1,[a,n]=O()?["0%{opacity:0;} 100%{opacity:1;}","0%{opacity:1;} 100%{opacity:0;}"]:[ea(r),en(r)];return{animation:t?`${x(a)} 0.35s cubic-bezier(.21,1.02,.73,1) forwards`:`${x(n)} 0.4s forwards cubic-bezier(.06,.71,.55,1)`}},el=s.memo(({toast:e,position:t,style:r,children:a})=>{let n=e.height?eo(e.position||t||"top-center",e.visible):{opacity:0},i=s.createElement(er,{toast:e}),o=s.createElement(es,{...e.ariaProps},T(e.message,e));return s.createElement(ei,{className:e.className,style:{...n,...r,...e.style}},"function"==typeof a?a({icon:i,message:o}):s.createElement(s.Fragment,null,i,o))});i=s.createElement,f.p=void 0,v=i,b=void 0,w=void 0;var ec=({id:e,className:t,style:r,onHeightUpdate:a,children:n})=>{let i=s.useCallback(t=>{if(t){let r=()=>{a(e,t.getBoundingClientRect().height)};r(),new MutationObserver(r).observe(t,{subtree:!0,childList:!0,characterData:!0})}},[e,a]);return s.createElement("div",{ref:i,className:t,style:r},n)},eu=(e,t)=>{let r=e.includes("top"),a=e.includes("center")?{justifyContent:"center"}:e.includes("right")?{justifyContent:"flex-end"}:{};return{left:0,right:0,display:"flex",position:"absolute",transition:O()?void 0:"all 230ms cubic-bezier(.21,1.02,.73,1)",transform:`translateY(${t*(r?1:-1)}px)`,...r?{top:0}:{bottom:0},...a}},ed=y`
  z-index: 9999;
  > * {
    pointer-events: auto;
  }
`,ef=({reverseOrder:e,position:t="top-center",toastOptions:r,gutter:a,children:n,toasterId:i,containerStyle:o,containerClassName:l})=>{let{toasts:c,handlers:u}=L(r,i);return s.createElement("div",{"data-rht-toaster":i||"",style:{position:"fixed",zIndex:9999,top:16,left:16,right:16,bottom:16,pointerEvents:"none",...o},className:l,onMouseEnter:u.startPause,onMouseLeave:u.endPause},c.map(r=>{let i=r.position||t,o=eu(i,u.calculateOffset(r,{reverseOrder:e,gutter:a,defaultPosition:t}));return s.createElement(ec,{id:r.id,key:r.id,onHeightUpdate:u.updateHeight,className:r.visible?ed:"",style:o},"custom"===r.type?T(r.message,r):n?n(r):s.createElement(el,{toast:r,position:i}))}))},ep=R}},function(e){var t=function(t){return e(e.s=t)};e.O(0,[774,179],function(){return t(6840),t(3079)}),_N_E=e.O()}]);