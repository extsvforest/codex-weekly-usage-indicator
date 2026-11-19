# Claude 5.11492.5.0 deletion-protection review (2026-11-14)

Bundle: `C:\Program Files\WindowsApps\Claude_5.11492.5.0_x64__pzs8sxrjxfjjc\app\resources\app.asar` (read in place, never copied). Offsets are asar byte offsets.

Chunks: manager in `.vite/build/index.chunk-CjUF9KtU.js` (start 13134865), header `Gt=require("./index.chunk-Q2iYtP6L.js")` at 13141543; removal/aside/finish in `index.chunk-Q2iYtP6L.js` (start 27664983), exports `exports.c=Ge,exports.d=We,exports.f=Ze,exports.i=Ke,exports.s=qe,exports.u=tt,exports.l=Qe`.

Before the change the live probe read `contract: Unreviewed`, flows 2, 4, 5, 6, 7, 8 (0-based) unmatched: minifier dropped arrow parens (`.catch(()=>!0)`), new `reapAfter:r`, removal/aside/finish moved to another chunk (`Gt.f`, `Gt.d`, `Gt.i`), new `currentOrgOnly` in the walk, aside gains `localIdsClaimedByOthers(...,{adopting:!0})`.

## a. Every id of a record is a claim — YES
14239276 (NB):
`for(let n of[r.cliSessionId,r.unarchivedCliSessionId])typeof n=="string"&&(!i?.localOnly||t.JOt(n))&&e.add(n);for(let t of[...Fe.i(r.priorCliSessionIds)??[],...Fe.i(r.overflowCliSessionIds,5e3)??[],...])e.add(t)`
Unparsable/oversized records throw under `failClosed`.

## b. Only own loaded records are skipped; currentOrgOnly never reaches claims — YES
14237185: `if(!n.isDirectory()||i?.currentOrgOnly&&n.name!==r().accountId)continue;`
14237608: `u=a===void 0&&n.name===r().accountId&&s.name===r().orgId;if(i?.currentOrgOnly&&!u)continue;`
`u` otherwise gates only `skipOwnRecordsOf`. `currentOrgOnly` is set only by MB (recovery index, ~14236695).
15391340: `cliSessionIdsClaimedOnDisk(){... let i={failClosed:!0,localOnly:!0,recordsOnly:!0,skipOwnRecordsOf:new Set([...this.sessions.keys(),...this.deletingSessionIds.keys()])}` then `NB(e,...,i)` and foreign roots with `{...i,foreignRootDir:t}`; error returns null (fail closed).

## c. Delete goes through the reap decision, which only adds refusals — YES
14733851 (HX): `mayRelease:async t=>!((await e.claimedOnDisk())?.has(t)??!1),reapAfter:r,reapIf:async(o,s)=>r!==void 0&&!await r?RX(...):BX(e,o,s,i,t,FX(n,o))`
BX (just before): `u===null?c("records_unreadable"...):u.has(n)?c("claimed_elsewhere"...)` then idle/running/written-since refusals; RX logs and returns `!1`.
15270573: `let c=HX(this.reapChecks,e,g,D,r.reapTranscriptsIdleSince)` ... `Gt.f(this.diskTranscript,O.filter(e=>!t.has(e)),Gt.x(g),"delete",c)`.

## d. Removal removes nothing unless reapIf says yes — YES
27696515 (Ze): `if(o===null||!g||!a.reapIf)return;await n(a.reapAfter);let _=!1;try{..._=await a.reapIf(u,{...})}catch(e){...debug("desktop-released marker: reap check failed"...);try{a.keptInDoubt?.(u)}catch{}}if(!_)return;`
`n` is the lock helper: frees a slot, awaits, swallows errors, retakes. After yes: transcript, `<id>/`, Ce (sidecars/stores), then Be (orphan `<id>/` elsewhere, only if no transcript of the id anywhere and listing complete).

## e. Set-aside path restores unless a second check passes; finish only writes markers — YES
UX ~14734269 builds the aside, ends `return await Gt.f(o.resolver,o.ids.filter(...),o.hints,"delete",{mayRelease:l.mayRelease}),k}` (no reapIf, markers only).
We (~27693500): refuses unless `mayRelease`; `p>r.notWrittenSince||!await r.reapIf(...).catch(()=>(r.keptInDoubt?.(t),!1))` → stop; any refusal restores via Ge/q (q refuses EEXIST).
Ke (~27694800): `let s=await r(e).catch(()=>!0)`; keeps and puts back unless all checks pass; then marker, rm asidePath, Re/Be only if `Ve(e)`.

## Removal sites (77 hunted in main-build JS)
- Through the decision: Ze, We, Ke, Re, Be, Ce; manager deleteSession (LocalSessions.delete), Device Storage age cleanup (DL8fZzVo ~24843571), importer "Remove" (KTuAzTbd ~27330242), prewarm discard.
- Protected separately: `tt` load scan (27698600–27700000) removes `<id>.jsonl.desktop-deleted-<nonce>` older than a day only when `VX` (on-disk ∪ in-memory claims; null keeps all) lacks the id and it is not being set aside. `qe` restores.
- Unrelated: SSH mirror sync/sweeper (`ssh-*`), imports (fresh randomUUID ids, private staging), cowork VM `.claude/projects`, SDK `deleteSession` (Bnr3jiHR ~7096686, not called by the app), records, `.import-*.tmp`, logs, lease, scratch workspaces, fork cleanup of its own new file.
- Uncertain: worktree transcript migration (~13675687) copies then unlinks the source (a move); `copyFile` without EXCL can overwrite an existing destination unless `existingDstIsAuthoritative`. Not checked against an older bundle.
- Uncertain: `skipOwnRecordsOf` now includes `deletingSessionIds`; not traced that these answer through in-memory claims (own account only).
