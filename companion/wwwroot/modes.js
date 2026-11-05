'use strict';
// One navigation boundary for the board, contextual controls, shared ink, Windows pen and display view.
(function(){
  const baseSwitch=setComputerMode,baseRender=renderComputer;
  let current='studio',switching=false;
  const selector=()=>document.getElementById('app-mode');
  function reflect(){
    if(selector())selector().value=current;
    document.body.classList.toggle('display-workspace-mode',current==='display');
    if(current==='display'){
      const heading=document.querySelector('#computer-workspace .computer-heading h2');
      if(heading)heading.textContent='Second display';
      const display=document.getElementById('computer-display');if(display)display.open=true;
    }
    if(desktopConsole){
      if(current==='computer'||current==='pen'||current==='pentablet'||current==='display'){
        document.querySelectorAll('.console-nav button').forEach(button=>button.removeAttribute('aria-current'));
        document.getElementById(current==='pen'?'console-pen':current==='pentablet'?'console-pen-tablet':current==='display'?'console-display':'console-computer')?.setAttribute('aria-current','page');
        document.getElementById('console-title').textContent=panel.open?document.getElementById('panel-title').textContent:current==='pen'?'Ink & notes':current==='pentablet'?'Pen tablet':current==='display'?'Second display':'Computer Copilot';
      }
    }
  }
  renderComputer=function(...args){baseRender(...args);reflect();};
  async function change(value){
    const next=['studio','computer','pen','pentablet','display'].includes(value)?value:'studio';
    if(switching){reflect();return false;}
    if(typeof DeckEditor!=='undefined'&&DeckEditor.isOpen()&&DeckEditor.isSaving?.()){notice('Wait for the board save to finish before changing workspaces.',true);reflect();return false;}
    if(next===current){reflect();return true;}
    if(!computerNavigationAllowed()){reflect();return false;}
    switching=true;
    try{
      // The underlying studio switch is a no-op when already on a board; protect its draft here too.
      if(dirty){
        if(!await confirmAction('Discard unsaved changes?','Your saved board stays unchanged. Discard the current edits to change workspaces.')){reflect();return false;}
        if(!computerNavigationAllowed()){reflect();return false;}
        if(typeof DeckEditor!=='undefined'&&DeckEditor.isOpen())DeckEditor.discardDraft();
        dirty=false;
      }
      const destination=next==='computer'||next==='display'?'computer':'studio';
      if(!await baseSwitch(destination)){reflect();return false;}
      editing=false;if(panel.open)panel.close();
      if(typeof setMonitorOpen==='function')setMonitorOpen(false);
      if(next!=='display'&&typeof stopComputerDisplayView==='function')stopComputerDisplayView();
      window.DeskPen?.setActive(next==='pen');
      window.DeskPenTablet?.setActive(next==='pentablet');
      current=next;reflect();
      if(next==='display')await refreshComputerDisplay();
      return true;
    }catch(error){notice(error.message||'The workspace could not open. Try again.',true);reflect();return false;}
    finally{switching=false;}
  }
  setComputerMode=change;
  window.setWorkspaceMode=change;
  window.DeskWorkspace=Object.freeze({get mode(){return current;},refresh:reflect});
  document.addEventListener('DOMContentLoaded',()=>{
    if(selector())selector().onchange=()=>change(selector().value);
    document.getElementById('console-pen')?.addEventListener('click',()=>change('pen'));
    document.getElementById('console-pen-tablet')?.addEventListener('click',()=>change('pentablet'));
    document.getElementById('console-display')?.addEventListener('click',()=>change('display'));
  },{once:true});
})();
