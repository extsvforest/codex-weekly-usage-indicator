const app = document.querySelector('#app');
const accounts = [
  {name:'메인 계정',remaining:63,date:'09.24 20:40',relative:'2일 6시간 뒤',wash:'#efe1d2',tone:'#9c765e'},
  {name:'서브 계정',remaining:81,date:'09.26 20:40',relative:'4일 6시간 뒤',wash:'#e3e8eb',tone:'#7b8a9d'},
  {name:'서브 계정2',remaining:24,date:'09.28 20:40',relative:'6일 6시간 뒤',wash:'#eee6cd',tone:'#ad965d'}
];
let current=0, selected=0, mode='ready', direction='a', confirmAction=null, refreshTimer=null;
const escapeHtml = value => value.replace(/[&<>"']/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
function render(){
  document.querySelector('#accounts').innerHTML = accounts.map((a,i)=>`<article class="account ${i===current?'active':''} ${mode==='partial'&&i===1?'failed':''}" style="--wash:${a.wash};--tone:${a.tone}">
    <div class="identity"><span class="avatar">0${i+1}</span><div><div class="name-line"><h3 title="${escapeHtml(a.name)}">${escapeHtml(a.name)}</h3></div><div class="account-meta">${mode==='partial'&&i===1?'확인 실패 · 이전 값':i===current?'<span class="active-dot"></span>사용 중':'14:30 확인'}</div></div></div>
    <div class="weekly"><div class="card-weekly-label">주간 잔여</div><div class="number">${mode==='partial'&&i===1?'이전 ':''}${a.remaining}<span>%</span></div><div class="meter"><i style="width:${a.remaining}%"></i></div></div>
    <div class="reset"><div class="reset-date">${a.date}</div><div class="reset-relative">${a.relative}</div></div>
    <div class="actions">${i===current?'<span class="using">사용 중</span>':`<button class="switch" data-switch="${i}" aria-label="${escapeHtml(a.name)}으로 전환" ${mode==='loading'?'disabled':''}>전환 <span aria-hidden="true">↗</span></button>`}<button class="more" data-more="${i}" aria-label="${escapeHtml(a.name)} 관리" ${mode==='loading'?'disabled':''}>···</button></div></article>`).join('');
  document.querySelector('#total').textContent=mode==='partial'?'—':'168';
  document.querySelector('#capacity').textContent=mode==='partial'?'전체 확인 필요':'/ 300%';
  document.querySelector('#coverage').innerHTML=mode==='partial'?'확인된 잔여 87% <span>·</span> 2/3개 계정':'3개 계정 '+(mode==='loading'?'이전 확인값':'모두 확인')+' <span>·</span> 계정당 100% 기준';
  const line=document.querySelector('.statusline');line.className='statusline '+(mode==='partial'?'warn':mode==='loading'?'loading':'');
  document.querySelector('#status-text').innerHTML=mode==='partial'?'서브 계정을 확인하지 못했어요. 이전 값은 보관했습니다.':mode==='loading'?'<span class="check">◌</span> 전체 조회 중 · 1/3개 확인 · 이전 값을 표시합니다.':'<span class="check">✓</span> 오늘 14:30에 전체 확인';
  const stateAction=document.querySelector('#status-action');stateAction.hidden=mode==='ready';stateAction.textContent=mode==='partial'?'전체 다시 갱신':'조회 취소';
  document.querySelector('#refresh-text').textContent=mode==='loading'?'조회 중':'전체 갱신';
  document.querySelector('[data-action=refresh]').disabled=mode==='loading';document.querySelector('[data-action=add]').disabled=mode==='loading';
  app.classList.toggle('loading',mode==='loading');document.querySelector('#state').value=mode;
}
function setMode(next){clearTimeout(refreshTimer);mode=next;document.querySelector('#menu').hidden=true;render();}
function refresh(){setMode('loading');refreshTimer=setTimeout(()=>setMode('ready'),2400);}
function dialog(title,body,confirm,action,name){
  document.querySelector('#menu').hidden=true;document.querySelector('#dialog-title').textContent=title;document.querySelector('#dialog-body').textContent=body;
  document.querySelector('#name-label').hidden=name===undefined;document.querySelector('#alias').value=name??'';
  document.querySelector('#confirm').hidden=!confirm;document.querySelector('#confirm').textContent=confirm||'';confirmAction=action;
  document.querySelector('#modal').hidden=false;if(name!==undefined)document.querySelector('#alias').focus();else document.querySelector('#dismiss').focus();
}
function closeDialog(){document.querySelector('#modal').hidden=true;}
function choose(view){direction=view;app.classList.toggle('direction-a',view==='a');app.classList.toggle('direction-b',view==='b');document.querySelectorAll('[data-view]').forEach(b=>b.setAttribute('aria-pressed',String(b.dataset.view===view)));document.querySelector('#critique').innerHTML=view==='a'?'<b>A · 추천</b><span>한 장의 밝은 바탕, 일정한 열, 가벼운 동작 버튼. 전체 잔여량을 보고 곧바로 계정을 비교하기 좋습니다.</span>':'<b>B · 대안</b><span>세 계정을 각자의 작은 종이 카드로 다룹니다. 세 개일 때 균형이 좋고 전환 대상이 또렷하지만, 계정이 늘거나 이름이 길어지면 목록보다 불리합니다.</span>';document.querySelector('#menu').hidden=true;}
document.addEventListener('click',event=>{
  const button=event.target.closest('button');if(!button){if(!event.target.closest('.popover'))document.querySelector('#menu').hidden=true;return;}
  if(button.dataset.view)choose(button.dataset.view);
  if(button.dataset.action==='refresh')refresh();
  if(button.dataset.action==='cancel'){if(mode==='partial')refresh();else {setMode('ready');document.querySelector('#status-text').textContent='조회 취소 · 이전 확인값을 유지합니다.';}}
  if(button.dataset.action==='reload'){document.querySelector('#status-text').textContent='저장된 목록을 다시 읽었습니다. 사용량은 새로 조회하지 않았습니다.';}
  if(button.dataset.action==='add')dialog('다른 계정 추가','실제 앱에서는 계정 이름을 정하고 브라우저 로그인으로 이어집니다.\n이 시안에서는 화면 구성만 확인할 수 있어요.','확인',closeDialog,'새 계정');
  if(button.dataset.switch!==undefined){const index=Number(button.dataset.switch);dialog('계정을 전환할까요?',`${accounts[current].name} → ${accounts[index].name}\n실제 앱에서는 Codex 종료와 전환 준비 상태를 먼저 확인합니다.`, '시안에서 전환',()=>{current=index;render();closeDialog();});}
  if(button.dataset.more!==undefined){selected=Number(button.dataset.more);const menu=document.querySelector('#menu');menu.hidden=false;document.querySelector('#menu-name').textContent=accounts[selected].name;menu.querySelector('[data-menu=delete]').disabled=selected===current;const b=button.getBoundingClientRect(),a=app.getBoundingClientRect();menu.style.left=Math.min(b.right-a.left-185,720)+'px';menu.style.top=Math.min(b.bottom-a.top+5,490)+'px';}
  if(button.dataset.menu==='rename')dialog('계정 이름 변경','목록에서 알아보기 쉬운 이름으로 바꿔주세요.','저장',()=>{const value=document.querySelector('#alias').value.trim();if(value){accounts[selected].name=value;render();closeDialog();}},accounts[selected].name);
  if(button.dataset.menu==='details')dialog(accounts[selected].name,'5시간 잔여 92%\n초기화 09.22 18:40\n마지막 확인 09.22 14:30\n\n시안용 가상 데이터입니다.',null,null);
  if(button.dataset.menu==='usage'){document.querySelector('#menu').hidden=true;document.querySelector('#status-text').textContent=accounts[selected].name+' 확인 완료 · 시안용 동작';}
  if(button.dataset.menu==='delete')dialog('저장된 로그인 삭제',accounts[selected].name+'의 저장된 로그인을 삭제하는 확인 화면입니다.\n비교 시안에서는 계정 수를 유지합니다.',null,null);
  if(button.id==='dismiss')closeDialog();if(button.id==='confirm')confirmAction?.();
});
document.querySelector('#state').addEventListener('change',e=>setMode(e.target.value));
document.addEventListener('keydown',e=>{if(e.key==='Escape'){closeDialog();document.querySelector('#menu').hidden=true;}});
const params=new URLSearchParams(location.search);if(params.has('capture'))document.body.classList.add('capture');choose(params.get('view')==='b'?'b':'a');setMode(params.get('state')||'ready');
