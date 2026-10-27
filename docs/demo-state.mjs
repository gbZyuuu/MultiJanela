export const themes = [
 ['midnight','Midnight','Noturno','#10141C','#1A202B','#252E3C','#303C50','#98B6FF'],
 ['graphite','Graphite','Grafite','#141516','#202224','#2D3033','#383D42','#D1D7DE'],
 ['ocean','Ocean','Oceano','#0D1922','#142733','#203A49','#2A4758','#76D3F0'],
 ['forest','Forest','Floresta','#111D19','#1A2C24','#2A4034','#324F3F','#9AE0AF'],
 ['amethyst','Amethyst','Ametista','#191422','#272033','#382E49','#493B5B','#C9ADFA'],
 ['rose','Rose','Rosé','#21171C','#32232B','#46313D','#533A47','#F1B1C5'],
 ['sand','Sand','Areia','#F2EEE6','#FFFCF5','#E8E1D5','#D5CABB','#79582D',true],
 ['light','Light','Claro','#F1F4F8','#FFFFFF','#E4EBF3','#CBD5E3','#315EA0',true],
 ['contrast','High contrast','Alto contraste','#000000','#080808','#171717','#999999','#FFFF00']
];
export const games = [
 {id:'101',name:'Sky Islands',genre:['Adventure','Aventura'],color:'#8fc7fa',people:'12.4K',shape:'islands'},
 {id:'102',name:'Neon Circuit',genre:['Racing','Corrida'],color:'#c6a3f5',people:'8.2K',shape:'circuit'},
 {id:'103',name:'Forest Stories',genre:['Exploration','Exploração'],color:'#a3dcb4',people:'6.8K',shape:'forest'},
 {id:'104',name:'Brick City',genre:['Roleplay','Roleplay'],color:'#e8b28a',people:'9.1K',shape:'city'},
 {id:'105',name:'Deep Blue',genre:['Discovery','Descoberta'],color:'#83dbe8',people:'4.6K',shape:'ocean'},
 {id:'106',name:'Moonbound',genre:['Adventure','Aventura'],color:'#bbbdf0',people:'3.2K',shape:'moon'}
];
export const outfits = [
 {id:1,name:'Daydream',shirt:'#8bafee',skin:'#f3c39b',hat:true,bag:false},
 {id:2,name:'Explorer',shirt:'#87c5a0',skin:'#f3c39b',hat:false,bag:true},
 {id:3,name:'After hours',shirt:'#b5a1e8',skin:'#bd8d69',hat:true,bag:true},
 {id:4,name:'Weekend',shirt:'#efabbd',skin:'#f3c39b',hat:false,bag:false}
];
export function freshState(){return {
 view:'accounts',account:1,tab:'friends',tool:'batches',theme:'midnight',motion:true,scale:100,global:{cpu:65,fps:60,quality:3},
 accounts:['Aurora','Nova','Orbit','Atlas','Luna','Cosmo'].map((name,i)=>({id:i+1,name,avatar:i,cpu:null,fps:null,quality:null,version:null,volume:80,muted:false,look:{...outfits[i%4]}})),
 sessions:[{id:1,pid:18240,game:'Sky Islands',version:'0.744.0.7441080',selected:false,seconds:1800},{id:2,pid:18264,game:'Sky Islands',version:'0.744.0.7441080',selected:false,seconds:420}],
 queue:[1,2,3],destination:'Sky Islands',presets:[{id:1,name:'Adventure crew',queue:[1,2,3],destination:'Sky Islands'}],
 accountGroups:[],metadata:{},favorites:[],friends:[{id:1,name:'Stella',game:'Sky Islands',alias:''},{id:2,name:'Kite',game:'Forest Stories',alias:''},{id:3,name:'Milo',game:'Neon Circuit',alias:''},{id:4,name:'Sunny',game:'',alias:''},{id:5,name:'Fern',game:'',alias:''}],
 groups:[{id:1,name:'Adventure club',members:1240,joined:true},{id:2,name:'Night builders',members:860,joined:true},{id:3,name:'Pixel creators',members:420,joined:false}],
 outfits:structuredClone(outfits),version:'0.744.0.7441080',history:[],progress:[],api:false,nexus:false,backgroundMute:false,macro:false
};}
export function effective(state,account,key){return account[key]??state.global[key]}
export function transition(state,action){
 const s=structuredClone(state); const id=Number(action.id), a=s.accounts.find(a=>a.id===id);
 switch(action.type){
 case 'view':s.view=action.value;break;
 case 'global':if(['cpu','fps','quality'].includes(action.key)){const v=Number(action.value);if(Number.isFinite(v))s.global[action.key]=Math.max(action.key==='cpu'?1:0,Math.min(action.key==='cpu'?100:action.key==='fps'?240:10,v));}break;
 case 'individual':if(a&&['cpu','fps','quality'].includes(action.key))a[action.key]=action.value===null?null:Math.max(action.key==='cpu'?1:0,Math.min(action.key==='cpu'?100:action.key==='fps'?240:10,Number(action.value)||0));break;
 case 'launch':if(a&&!s.sessions.some(x=>x.id===id)){s.sessions.push({id,pid:18240+id*37,game:action.game||s.destination||'Roblox Home',version:a.version||s.version,selected:false,seconds:0});s.history.unshift({name:a.name,game:action.game||s.destination,event:'launch'});s.progress=[...new Set([...s.progress,'launch'])];}break;
 case 'close':s.sessions=s.sessions.filter(x=>x.id!==id);break;
 case 'select':s.sessions=s.sessions.map(x=>x.id===id?{...x,selected:!x.selected}:x);break;
 case 'select-all':s.sessions.forEach(x=>x.selected=!!action.value);break;
 case 'close-selected':s.sessions=s.sessions.filter(x=>!x.selected);break;
 case 'close-all':s.sessions=[];break;
 case 'queue-add':if(a&&!s.queue.includes(id)){s.queue.push(id);s.progress=[...new Set([...s.progress,'queue'])];}break;
 case 'queue-remove':s.queue=s.queue.filter(x=>x!==id);break;
 case 'queue-clear':s.queue=[];break;
 case 'queue-move':{const i=s.queue.indexOf(id),j=Math.max(0,Math.min(s.queue.length-1,i+Number(action.delta)));if(i>=0&&i!==j){s.queue.splice(i,1);s.queue.splice(j,0,id);}break;}
 case 'queue-place':{const i=s.queue.indexOf(id),j=s.queue.indexOf(Number(action.target));if(i>=0&&j>=0&&i!==j){s.queue.splice(i,1);s.queue.splice(j,0,id);}break;}
 case 'theme':if(themes.some(t=>t[0]===action.value)){s.theme=action.value;s.progress=[...new Set([...s.progress,'theme'])];}break;
 case 'favorite':s.favorites=s.favorites.includes(action.value)?s.favorites.filter(g=>g!==action.value):[...s.favorites,action.value];break;
 case 'friend-remove':s.friends=s.friends.filter(f=>f.id!==id);break;
 case 'friend-alias':{const f=s.friends.find(f=>f.id===id);if(f)f.alias=String(action.value).trim().slice(0,30);break;}
 case 'group':{const g=s.groups.find(g=>g.id===id);if(g){g.joined=!g.joined;g.members+=g.joined?1:-1;}break;}
 case 'group-save':{const gid=Number(action.groupId);const found=s.accountGroups.find(g=>g.id===gid);const group={id:gid||Math.max(0,...s.accountGroups.map(g=>g.id))+1,name:String(action.name).trim().slice(0,80)||'Group',accounts:[...action.accounts]};if(found)Object.assign(found,group);else s.accountGroups.push(group);break;}
 case 'group-delete':s.accountGroups=s.accountGroups.filter(g=>g.id!==Number(action.groupId));break;
 case 'metadata-save':if(a)s.metadata[id]={tags:String(action.tags).slice(0,1000),notes:String(action.notes).slice(0,4000),fields:String(action.fields).slice(0,12000)};break;
 case 'preset-update':{const p=s.presets.find(p=>p.id===id);if(p){p.name=String(action.name).trim().slice(0,80)||p.name;p.destination=String(action.destination).slice(0,2048);p.queue=[...action.accounts];p.delay=Number(action.delay)||0;p.cpu=Number(action.cpu)||100;}break;}
 case 'preset-save':if(s.queue.length)s.presets.push({id:Math.max(0,...s.presets.map(p=>p.id))+1,name:String(action.value).trim().slice(0,40)||'My preset',queue:[...s.queue],destination:s.destination});break;
 case 'preset-load':{const p=s.presets.find(p=>p.id===id);if(p){s.queue=[...p.queue];s.destination=p.destination;}break;}
 case 'preset-delete':s.presets=s.presets.filter(p=>p.id!==id);break;
 case 'look':if(a)a.look={...action.value};break;
 case 'account-rename':if(a)a.name=String(action.value).trim().slice(0,25)||a.name;break;
 case 'account-move':{const i=s.accounts.findIndex(a=>a.id===id),j=Math.max(0,Math.min(s.accounts.length-1,i+Number(action.delta)));if(i>=0&&i!==j){const [item]=s.accounts.splice(i,1);s.accounts.splice(j,0,item)}break;}
 case 'account-remove':s.accounts=s.accounts.filter(a=>a.id!==id);s.sessions=s.sessions.filter(x=>x.id!==id);s.queue=s.queue.filter(x=>x!==id);s.accountGroups.forEach(g=>g.accounts=g.accounts.filter(x=>x!==id));s.presets.forEach(p=>p.queue=p.queue.filter(x=>x!==id));if(s.account===id)s.account=s.accounts[0]?.id||0;break;
 case 'account-add':if(s.accounts.length<12){const n=Math.max(0,...s.accounts.map(a=>a.id))+1;s.accounts.push({id:n,name:String(action.value).trim().slice(0,25)||`Player ${n}`,avatar:(n-1)%6,cpu:null,fps:null,quality:null,version:null,volume:80,muted:false,look:{...outfits[0]}});}break;
 }
 return s;
}
