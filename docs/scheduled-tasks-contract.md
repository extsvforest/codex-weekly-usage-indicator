# Claude Desktop 6.28899.6.0: how the app keeps the scheduled-tasks registry

Read on 2026-11-21 for the widget's scheduled-task mirror. It records what the app itself does; what the widget
does with it is in the section "What the widget's mirror does with this" below and in `AGENTS.md`. Offsets and minified names
are those of this build only.

Bundle: `%ProgramFiles%\WindowsApps\Claude_6.28899.6.0_x64__pzs8sxrjxfjjc\app\resources\app.asar`, read in place through a
read-only mmap. Nothing was extracted to disk. No real registry, SKILL.md, session or auth file was opened: every answer
below comes from the bundle code. Offsets are absolute byte offsets in app.asar. Names are the minified ones.

Chunks used:
- `index.chunk-CCkEi--u.js`: the task manager class `_ps` (17080730), its zod schemas, paths and file helpers, and the
  IPC handlers.
- `index.chunk-DvDp99eh.js`: the Code (CCD) session manager. It holds the account hooks, the run-start mode checks and
  the carried-mode decision table.
- `index.chunk-CAQdzOoT.js`: the scheduled-tasks MCP tools (create, update, delete, run).
- `index.chunk-CIWixCcw.js`: the launch environment. `index.chunk-Bmi2XdQp.js`: who started the current turn.
- `index.chunk-BhcPzNzB.js`: the app's own task importer (`[claude-ai-import:tasks]`), seen in passing.

The Code tasks manager is `C8`, exported as `t.Ul`. Its config is at 17188094:
`telemetryPrefix:"desktop_ccd",...,getStorageFilePath:yps,getTaskFilesDir:WKo`. Cowork tasks have their own manager
(`S8`, `t.Wl`) and their own files, and are out of scope here.

| What | Where | Code |
|---|---|---|
| Registry | `yps` 17185184 | `n.default.join(u.app.getPath("userData"),Qdt,e,t,"scheduled-tasks.json")`, `Qdt="claude-code-sessions"` (11661365). One file per account and org. |
| Definitions | `WKo` 16629756, `ZL` 14417153 | `(0,n.join)(ZL(),"scheduled-tasks")`. `ZL` is `CLAUDE_CONFIG_DIR`, otherwise `~/.claude`. `WKo` ignores the account and org arguments: **every account shares one definitions folder.** |

## 1. When the registry is read and written

### Reads

There is one reader, `loadScheduledTasksFromDisk` (17105182). Only `initialize(account, org)` (17159245) calls it, once
per load:

```
initialize(e,t){if(this._initializePromise){j.warn(`${this.config.logPrefix} Already initialized`);return}aps(),this._loadedFor={accountId:e,orgId:t};let r=this.config.getStorageFilePath(e,t);this._scheduledTasksFilePath=r;let i=this.config.getTaskFilesDir(e,t);
```

`initialize` is driven by the session manager in DvDp99eh:

- **At app start**, after sign-in, `onInitialized(e,n)` calls `t.Ul.initialize(e,n)` (28848761).
- **On an account or org change**, `onAccountOrgChanged` first runs `t.Ul.reset()` (28846874). `reset()` (17163369)
  bumps `_initCounter`, clears the task map, the path and `_loadedFor`, and stops the intervals and timers. Then
  `onInitialized` loads the new account's file.
- **There is no file watcher and no periodic reload.** Create, edit and run never re-read the file.

Load does the following (initialize, 17159245–17163126):

- The file is parsed with the zod schema `Tfs` (17077654). `scheduledTasks:O(vfs)` has no per-entry catch, so one
  invalid entry fails the whole file, and the result is `{scheduledTasks:[],loadFailed:!0}`.
- Fields with no `.catch(void 0)` must have the right type. These include `id`, `enabled`, `filePath`, `createdAt`,
  `cronExpression`, `fireAt`, `lastRunAt`, `lastScheduledFor`, `missedRunScanFloor`, `cwd`, `approvedPermissions`,
  `notifySessionId`, and `permissionMode`. `permissionMode` is `Vse(fl)`: `default`, `acceptEdits`, `plan`,
  `bypassPermissions`, `dontAsk` or `auto` (enum at 11268244).
- Ids not matching `F3=/^[a-z0-9_-]+$/` (16631582) are dropped: "Dropping scheduled task with unsafe id".
- `filePath` is overwritten with `join(taskFilesDir, id, "SKILL.md")`, so the stored value is ignored.
- The two matcher-upgrade stamps are applied (see 2), and skips and retries are kept only for ids present in the file.
- Then the loop starts: `setInterval(...,jfs)` with `jfs=6e4` (17078332), plus an immediate `this.checkScheduledTasks()`.

### Writes

The single writer is `persistToDisk` (17105489), which calls `writeSnapshotToDisk` (17105636). It writes the whole
in-memory snapshot and merges nothing from disk:

```
return await va(t,{scheduledTasks:Array.from(this._scheduledTasks.values()),recordedSkips:r,...Object.keys(i).length>0&&{recordedPlaceSkips:i},...this._runRetries.size>0?{runRetries:Object.fromEntries(this._runRetries)}:{},...this._sundayAliasBoundaryStamped?{sundayAliasBoundaryStamped:!0}:{},...this._dayFieldsOrBoundaryStamped?{dayFieldsOrBoundaryStamped:!0}:{}}),!0
```

How the file is written:

- `va` (10912232) calls `pa` (10906688) with `JSON.stringify(t,null,2)` and mode `384` (0600).
- `pa` writes a temp file and renames it over the target, retrying on Windows sharing errors. When the rename keeps
  failing, it falls back to writing in place.

Every mutation persists, so the file is rewritten often:

- Create, through `saveScheduledTask`, and `addImported`.
- `update`, `enable`, `disable` and `delete`.
- `updateLastRun`. This runs at every cron run start in `emitSessionStarted` (17144545) when ack mode is off, or at
  `bookTaskRun` (17122642) when the page acknowledges.
- `notePromptWriter`, the save in `watchTaskFileUpdate`, `stampChangedByClaude`, the load-time stamp, and the retry and
  skip changes.

### Other accounts' registries

- **Holding:** the manager holds only one registry. There is one `_loadedFor` and one `_scheduledTasksFilePath`, and
  `reset()` drops both.
- **Writing:** it writes only the loaded path. `writeSnapshotToDisk` stops with "persistToDisk aborted — superseded by
  reset()" when `e!==this._initCounter||t!==this._scheduledTasksFilePath`.
- Two result paths refuse to write into a registry that is no longer loaded:
  - `recordObservedToolUse` (17179085) checks `this.config.getStorageFilePath(n.accountId,n.orgId)!==this._scheduledTasksFilePath`
    and logs "Dropping observed tool use for task ... account changed since run finish".
  - `listDispatchOriginTasksForMigration` (17142024) makes the same comparison.
- **Read only, elsewhere:**
  - The app's importer reads the `scheduled-tasks.json` of source folders from a moved-in install or export
    (BhcPzNzB, functions `A` and `j` near 7556000).
  - A cowork migration (near 10254400) reads every account's cowork registry, but only to collect ids.
  - Neither writes those files.

### External edits

- **Loaded account:** an edit is never seen, and the next persist overwrites it. The next cron run start is enough to
  trigger that persist.
- **Account not loaded:** an edit is read at that account's next `initialize`, which happens on a switch to it, or at
  app start while it is the signed-in account.
- **Small race (Claude reading, not observed):** a persist that has already passed its checks when `reset()` runs can
  still finish just after a switch.

### Failed load

- Init sets `this._manifestLoadFailed=d===!0`. `isManifestLoaded()` is false, and `addImported` refuses with
  "manifest_not_loaded".
- But `createAndSave` (17108201) checks only `guardInitialized`, which passes, and `writeSnapshotToDisk` has no such
  guard.
- So the first task created in that account rewrites the file with only the new entry, and **the unreadable entries
  are lost.**

## 2. What the fields mean and who uses them

| Field | Set by | Read by / effect |
|---|---|---|
| `permissionMode` | Create tool: `permissionMode:N` (10277717). UI update (see 5). Import: `carriedAuto` gives Auto. | Copied into the start event by `lcs` (17004256): `permissionMode:e.permissionMode`. Checked again at run start by `ur`/`dr` (see 3). |
| `permissionModeCarried` | Create tool: `...w?.mode!==void 0&&{permissionModeCarried:!0}`. `addImported`: `...e.carriedAuto===!0&&{permissionMode:fl.Auto,permissionModeCarried:!0}`. Schema `A(!0).optional().catch(void 0)`, so only `true`. | Marks a mode that came from the creating session, or an import, rather than from the person in the task editor. Read by `ur`/`dr` (3) and by cowork `th` (24854845). **Cleared** by any update that carries a `permissionMode` key, even an unchanged one: `"permissionMode"in n&&r.permissionModeCarried&&(delete r.permissionModeCarried,c=!0)` (17118820). |
| `madeByClaude` | Only the MCP create tool: `madeByClaude:!0`. `createAndSave` copies it. | Only the schema, that copy and the IPC record shape check (11523149) use it. No main-process behavior reads it; it reaches the page with the task list. What the page does with it is not in this bundle (uncertain). |
| `promptWrittenBy` | At create, from `u.promptWriter().id`, the creating CLI session. Appended by `notePromptWriter` (17109545) when Claude rewrites the prompt through the update tool (10282597). `"unknown"` is appended when the file no longer matches `promptFileHash` (`readTaskFileToHandOn` 17110325, `watchTaskFileUpdate` 17109678). `Yls` (17019348) maps bad ids to `"unknown"` and caps the list at 32. | `promptWriters` (17110088) returns the list, plus `undefined` on a hash mismatch. At launch it becomes `CLAUDE_CODE_LAUNCHED_BY_SESSION_IDS` (see 3). |
| `promptFileHash` | `Vds(wqo(id,prompt,description))` at create, refreshed by app-made writes. | Compared with `Hds`. A mismatch means someone outside the app's own writes changed SKILL.md, and adds an "unknown" writer. |
| `notifySessionId` | Create tool: `notifySessionId:(S??!0)&&!D?E:void 0`, the calling session when completion notices are on. Update tool: `w.notifySessionId=g?i():void 0` (10281388). | `isDispatchOriginTask` (17141420) resolves the session type: a Dispatch session makes the task "Dispatch-origin" and subject to `heldByDispatchSwitch`. Also used for inventory counts. `getNotifySessionId` is called only for cowork (24552069). No Code-task consumer was found in this bundle (uncertain; likely the page). |
| `lastRunAt` | `updateLastRun` (17120729): `r.lastRunAt=(new Date).toISOString()`. | Due-now check `sus` (17045290): the cron matches this minute and `lastRunAt` is before it. For a one-time task, `!e.lastRunAt` means not run yet. |
| `lastScheduledFor` | Same place: `r.lastScheduledFor=t?.toISOString()`. A manual run leaves it unset: `updateLastRun(e,n?.manual?void 0:n?.scheduledFor)` in `bookTaskRun`. | Base of the missed-run scan: `e.lastScheduledFor??e.lastRunAt` (see 6). |
| `missedRunScanFloor` | `stampMissedRunScanFloor` (17114012) sets it to now when: a disabled cron task is enabled (`applyEnable`, and `update` with `enabled`); `update` sets a new cron; `addImported` adds an enabled task. A new `fireAt` clears it along with `lastRunAt` and `lastScheduledFor`. | Lower bound of the missed-run scan in `cus` and `owedSlot` (see 6). |
| `recordedSkips` | File-level map, id to skips `{at,reason,detail,ticks,failures,notified,open,host}` (schema `bfs`). Only PerTaskLimit, GlobalLimit and SignInRequired (`Cfs`) go here; other reasons go to `recordedPlaceSkips`. Kept for 7 days (`Zfs=6048e5`) or while open. | Loaded only for ids that are present. Used to list skipped runs and to avoid recording the same skip twice (`recordSkipOnce` 17089001). |
| `sundayAliasBoundaryStamped`, `dayFieldsOrBoundaryStamped` | File-level, one-time markers, written as `true` by the next persist after a stamp. | When a flag is missing and the load succeeded, cron tasks affected by that matcher change (`rus`: day-of-week has 7 but not 0; `aus`: day fields OR'ed) get `lastScheduledFor` raised. The new value is `max(now−60 s` or `hps(now)` (the end of the last Sunday), `createdAt)`. The log says this is done "so a matcher upgrade (Sunday alias / day-field OR) doesn't back-fill a missed run". |
| `runRetries` (related) | File-level map, id to `{slot,attempts,notBefore,...}`. | Loaded for known ids, and `dueRunRetry` can start a retry run from it. |

## 3. How the run-time mode is decided

1. **Stored mode to start event.** `lcs` (17004256) builds the event:
   `{sessionId:t,model:e.model,useWorktree:e.useWorktree,sourceBranch:...,permissionMode:e.permissionMode}`.
   A local task with no `cwd` gets `null`, and the run is skipped ("buildSessionStartEvent returned null").
2. **The page starts the session.** The page is the remote web app. Only the preload (`mainView.js`) appears in the
   bundle, so it is inferred, not seen, that the page passes the stored mode on unchanged.
3. **Main process re-check.** `startSession` calls `normalizeStartOptionsAtIngress` (28974955), which ends with
   `ur(e,t.Ul),dr(e,t.Ul)`. Both functions look the task up in the *loaded* registry (`n.getSync(e.scheduledTaskId)`).

```
function cr(){return!t.yDt()||t.TDt("396417011")}var lr=e=>t.l4(e,t.GLt.Default);function ur(e,n){if(e.scheduledTaskId===void 0||!cr())return;let r=n.getSync(e.scheduledTaskId);(r?r.permissionModeCarried&&lr(e.permissionMode):t.md(e.permissionMode))&&(t.lWt.info(`[CCD] A scheduled run starts in Manual: kept modes are switched off (...)`),e.permissionMode=t.GLt.Default,e.permissionModeProvisional=!1)}
function dr(e,n){let r=e.scheduledTaskId===void 0?void 0:n.getSync(e.scheduledTaskId);!r?.permissionModeCarried||r.importedFrom===void 0||r.permissionMode!==t.GLt.Auto||(e.permissionModeProvisional=!1,e.permissionMode!==t.GLt.Auto&&lr(e.permissionMode)&&(...,e.permissionMode=t.GLt.Default))}
```

What the helpers mean:

| Helper | Resolves to | Meaning |
|---|---|---|
| `t.yDt` | `Vv` (12316079) | Feature flags are loaded. |
| `t.TDt` | `Y` | Gate check. |
| `t.md` | `fls` (17012729) | The mode is `auto` or `bypassPermissions`. |
| `t.l4` | `iR` (14423454) | The first mode ranks above the second, or either is unknown. Ranks: `nR={plan:0,default:1,dontAsk:1,acceptEdits:2,auto:3,bypassPermissions:4}`. |

What the two checks do:

- **`ur` is a kill switch.** It acts when flags are not loaded, or when gate 396417011 is on. Then a carried mode above
  Default is started in Manual (`default`). So is any `auto`/`bypassPermissions` start for a task id that the loaded
  registry does not hold.
- **`dr` limits imports.** A task that is imported, carried and Auto may start only in Auto. Anything else above
  Default becomes Manual.

Effect of a `promptFileHash` mismatch: **the app neither lowers the mode nor blocks the run.**

- The mismatch adds "unknown" to the writer list.
- At launch, `CLAUDE_CODE_LAUNCHED_BY_SESSION_IDS` is set from that list. The call is at 27310229:
  `p=Ln!==void 0&&_.s()&&t.Ul.isInitialized()?await t.Ul.promptWriters(Ln):void 0`.
- The value is built by `Cl` (18897932):
  `CLAUDE_CODE_LAUNCHED_BY_SESSION_IDS:e.length+n.length===0?",":gl()?t.td(e,n).join(","):t.ed`, with `t.ed="unknown"`.
- The other hits for this name in the bundle are only env-name tables. **What the CLI does with "unknown" is outside
  app.asar: uncertain.**

What the two provenance fields tell apart:

- `madeByClaude`: the record was created by Claude through the MCP create tool, not by the person in the page and not
  by an import.
- `promptWrittenBy`: who wrote the prompt text, kept up across later edits. It is either the known session ids or
  "unknown" (edited outside the app, or never recorded).
- Neither field changes the mode in the main process.

The run tool's text (10306558) describes a run as starting "with the task's stored prompt, permission mode, and the tool
approvals the user already granted that task".

## 4. When the create tool carries the session's mode

The create tool's handler is `P` (CAQdzOoT 10273494):

```
w=await u.carriedTaskMode?.(h,e) ... N=w?w.mode??(w.inManual?t.GLt.Default:w.inPlan?t.GLt.Plan:void 0):u.newTaskPermissionMode?.()
... n.create({...,permissionMode:N,...w?.mode!==void 0&&{permissionModeCarried:!0},madeByClaude:!0,...})
```

If `w.needsCard` is set, the tool refuses and asks for approval on a card instead.

How a local Code session gets `w`:

- The session passes `carriedTaskMode:this.deps.taskModeHereOf(n)` (27146465).
- That calls `taskModeHere.of` (`zh`, 26752070), which calls `carriedRoutineModeOf` (29172574), which calls `Ih`
  (26750022):

```
function Ih(e,n){if(n.switchedOff)return{row:1};if(e===void 0)return{row:2};if(!n.sessionStandsDown||!Ph(e))return{row:3};if(n.remoteControlLineage)return{row:4};if(n.routineRunsElsewhere)return{row:5};if(!n.turnIsPersonsAlone)return{row:6};let r=e.permissionMode;return(r===t.GLt.Auto||r===t.GLt.Bypass)&&n.available(r)?{mode:r,row:r===t.GLt.Auto?7:8}:{row:9}}
```

`carriedRoutineModeOf` supplies the inputs:

- `switchedOff:cr()`, the same kill switch as `ur`.
- `sessionStandsDown:!t.d$(e,...)&&this.permissionBroker.sessionStandsDownFor(t.fEt,e)`: the session would run the
  create tool without asking.
- `remoteControlLineage`.
- `routineRunsElsewhere:!q.An(r)&&!zh.cliRunsHere(r)`.
- `turnIsPersonsAlone:r!==void 0&&Y.P(r)`.

It then re-checks that the mode is available, with `yt.a(i,r.mode)`.

The mode is carried (rows 7 and 8) only when all of these hold:

- The switch is off.
- The session's mode is Auto or Bypass and was chosen in the app.
- The session would not ask for the tool.
- It is not remote-control lineage.
- The CLI runs on this computer.
- The turn is the person's alone.
- The mode is still available.

`of` (26752070) can change the result further:

- If the create call itself was asked on a card, the result becomes `{row:12}` and the mode is not carried.
- When nothing is carried and `permissionBroker.modelessCreateKeepsItsCard` is true, the task gets one of:
  - `inManual`, which saves `default`, when the session's mode is at least Default;
  - `inPlan`, which saves `plan`, for a Plan session;
  - otherwise `needsCard`, and the create is refused.
- In every other case, `N` is `undefined` and no mode is saved.

**Why it was not carried in a turn started by another session's message.** Row 6. `Y.P` is `$e` (Bmi2XdQp 8671997;
`U` at 8671933):

```
function U(e){return e.turnInput?.open===!0&&e.turnInput.person}function $e(e){return U(e)&&e.turnInput?.machine!==!0&&!N(e)}
```

`turnInput` is set at 8673148:
`t.turnInput={person:c&&s?.person===!0||n,machine:c&&s?.machine===!0||!n,...,open:!0,...}`.

- `n` is "this input came from the person". A turn opened by another session's message therefore gets `machine:true`.
- `c` is "the turn is open and running", so `machine` stays true for the rest of that turn, even if the person types
  into it.
- `N(e)` (8669440) is a further condition: an older active background task also makes the turn not the person's alone.

**Where a mode the person chose is told apart.** `Ph` (26749875), used at row 3:

```
Ph=e=>e.permissionMode===t.GLt.Bypass?e.bypassChosenInApp===!0:e.permissionMode!==t.GLt.Auto||e.autoChosenInApp===!0
```

`bypassChosenInApp` and `autoChosenInApp` are fields of the session record.

## 5. Can the page change a task's mode, and how is it saved?

The IPC contract allows it:

1. **Channel.** `CCDScheduledTasks.updateScheduledTask(params)` (definition 11602578; preload `mainView.js` 33293935:
   `ipcRenderer.invoke(X+"CCDScheduledTasks_$_updateScheduledTask",e)`).
2. **Parameter check.** `Hrt` (11525523) accepts `permissionMode` when `Bl(...)` passes.
3. **Handler** (17519771): `"permissionMode"in n&&(l.permissionMode=n.permissionMode)`.
4. **`e.assertUpdateAllowed(r,l)`** (17114909) checks only the too-late case, when a one-time task on another host is
   being enabled.
5. **`e.update(id,l)`** (17116237) sets the mode, deletes `permissionModeCarried` (quote in 2), and calls
   `persistToDisk`.

Whether the task editor shows a mode control cannot be decided from app.asar, because the page is remote. The main
process accepts the change.

The MCP update tool takes no mode: CAQdzOoT has three `permissionMode` hits, and only create sets one.

**Consequence:** a mode saved through the editor is no longer "carried", so the `ur` kill switch and the `dr` import
limit no longer apply to it.

## 6. When missed runs are caught up

### The tick

- It runs every 60 s and once right after load. The body is `checkScheduledTasksInner` (17151379).
- A run is started only after these gates pass:
  - runs are allowed and there are tasks;
  - stale dispatches are cleared (`Rfs=9e5`);
  - the post-wake delay is over (`dps`) and the computer is online (`x8`);
  - the page listener is ready. Until it is, "due runs are only checked for a missing task file or a sign-in, and none
    is started", logged after `Bfs=3e4`;
  - placement, then "ready to dispatch" (the CLI binary is present), then the throttle;
  - the unattended credential (sign-in) check;
  - the task file exists: `h.access(e.filePath)`, otherwise the task is skipped with "task file not found".

### Cron tasks

```
let n=sus(e.cronExpression,e.lastRunAt),i=n?null:this.missedCronSlot(e);
missedCronSlot(e){return e.cronExpression?cus(e.cronExpression,e.lastScheduledFor??e.lastRunAt,e.createdAt,e.missedRunScanFloor):null}
```

`cus` (17045483) does the scan:

- The base is `lastScheduledFor ?? lastRunAt`, or `createdAt` when both are absent.
- The floor is `max(base, now − 7 days, missedRunScanFloor)`.
- It walks back one minute at a time (`u8=6e4`) from the minute before now down to the floor, skipping the base's own
  minute. It returns the newest slot that matches the cron.
- That gives **at most one catch-up run, for the newest missed slot only**, started with no jitter:
  `h=i||s?0:this.getJitterSecondsForTask(e.id)`.

### One-time tasks

- `e.enabled&&!e.lastRunAt&&e.fireAt<=Date.now()` starts the run now.
- A task on another host that is more than 7 days late is switched off instead (`gateDueTask` 17089233).

The create tool's text confirms the behavior (10299786): "If the app is closed when a task is due, it runs on next
launch".

### A new entry with an old `lastRunAt`

- **If it appears while its account is loaded:** it is never seen, and the next persist removes it.
- **If it is in the file when the account loads:** **yes, it runs at once**, on the first tick that passes the gates,
  which can be the tick right at load. This happens when a matching slot lies after
  `max(lastScheduledFor ?? lastRunAt ?? createdAt, now − 7 days, missedRunScanFloor)`.
  - Only one run starts.
  - Nothing starts when the base is at or after the newest slot.
- **What the app does for its own imports:** `addImported` sets `createdAt:Date.now()` and
  `m.enabled&&this.stampMissedRunScanFloor(m)`, so an import never catches up old slots.

## 7. Duplicate-id checks

### MCP create

The checks run in this order (CAQdzOoT, from 10273494):

1. **Registry.** `if(await n.get(j))`, which answers "A scheduled task with ID "${j}" already exists. Use
   update_scheduled_task to modify it, or choose a different taskId." (10276471).
2. **Title.** `A` (10270311) answers "Another scheduled task already has that title. Choose a different title.", or "A
   scheduled task is already listed as ...".
3. **Definition folder.**
   - `t.Jh`=`Tqo` (16637316) calls `yqo` (16636448):
     `await h.mkdir(e).catch((t=>{throw t.code==="EEXIST"?new L3(`task files exist: ${(0,n.basename)(e)}`):t}))`.
     The mkdir is not recursive, so an existing `<definitions>/<id>/` throws `L3` (=`t.Ph`).
   - The tool turns that into "Nothing was created: the ID "${j}" is already in use on the user's computer (another
     task's folder has that name). Call create_scheduled_task again with taskId "${r}" ... and everything else the
     same." (10276919).
   - The suggested id comes from `t.Bh`=`Dqo` (16638092). It is the first of `<id>-2` to `<id>-20` (`pqo=20`, `mca`)
     that is neither in the registry nor present as a folder.

Because the definitions folder is shared, **an id used by any account, or a leftover folder, blocks that id in every
other account.** This is the refusal seen after an account switch.

### The app's import

`addImported` (17111853) returns:

| Return | When |
|---|---|
| `"bad_id"` | The id fails `F3`. |
| `"id_taken"` | The id is already in the registry. |
| `"already_imported"` | Another entry has the same `importedFrom`. |
| `"title_taken"` | The title is already used. |
| `"id_taken"` | The folder exists (`lstat` is not ENOENT), or the definitions folder is a symlink while its boundary is untrusted. |

After these checks it writes its own copy of the prompt through `Tqo`. The importer itself (BhcPzNzB `L`, about 7557600)
picks the id: first the source id, then `<id>-imported`, then numbered ids, each free both in the registry and on disk.

### Writing a task file

`Rqo` (16639030) refuses a hard-linked SKILL.md when gate 346871663 is on: "Not written: this task's file is hard-linked
to another file. Replace it with a plain copy."

## Deleting a task while the definitions are shared

- **MCP delete keeps the definition.** The tool text says "The task's SKILL.md file is left on the user's computer so
  the prompt can be recovered." (10305795). It also archives the task's sessions.
- **The page's delete can remove it.** `updateScheduledTaskStatus(id,"deleted",removeFiles)` calls `VPs` (17510368),
  which runs `t.delete(n)` and then, when `removeFiles===!0`, `Yqo` (16642727):
  `await h.rm(i,{recursive:!0,force:!0})` on `<definitions>/<id>`.
  - There is no check for other registries that list the same id.
  - A copy of that entry in another account then hits "task file not found" on every tick.
  - The handler passes `removeFiles:a?void 0:i`, and the flag `a` was not traced.

## What this means for a cross-account copy (Claude reading, not tested)

1. **When to write.** Write a registry only while its account is not loaded. The loaded account's file is overwritten
   by the next persist and is not re-read before a switch or a restart.
2. **How to write.**
   - Write atomically (temp file, then rename), and keep the whole file valid for `Tfs`.
   - One bad entry hides all of that account's tasks, and the next create in that account wipes them.
   - Copy only ids that match `^[a-z0-9_-]+$`.
3. **Definitions.** The same id means the same SKILL.md, so no definition needs copying. `filePath` is rewritten at
   load anyway.
4. **No duplicate runs.** To avoid a catch-up run of a slot the source account already ran, carry the newer of the two
   `lastScheduledFor`/`lastRunAt` values, or set `missedRunScanFloor` to the copy time (as `addImported` does).
5. **Modes.**
   - Copy `permissionMode` and `permissionModeCarried` together and unchanged. Dropping `permissionModeCarried` while
     keeping `auto`/`bypassPermissions` makes the mode look chosen in the editor, which takes it out of the
     396417011 kill switch.
   - Do not add `importedFrom`: it brings in the import rules (`dr`, and the update tool's refusal to re-enable an
     imported task that is switched off, 10281388).
6. **Run history.** `recordedSkips` and `runRetries` belong to the source account. Copying `runRetries` can start a
   retry run.
7. **Session ids.** `notifySessionId` and `promptWrittenBy` name sessions of the source account. The main process
   tolerates ids it does not know. What the CLI does with launched-by ids is not in the bundle.
8. **Deletes.** Deleting a copied task in one account through the page with "remove files" deletes the definition for
   every account.

## What the widget's mirror does with this (2026-11-21)

The section above is the reading before the design was settled. The mirror (`ClaudeScheduledSync`, described in
`AGENTS.md`) follows points 1 to 3 and 6 to 8, and departs from two:

- **Point 4.** It never carries run times. A copy is made with the source's `createdAt` and a `missedRunScanFloor` of
  the time it is made, as `addImported` does. Afterwards a pass raises a copy's floor just past the newest slot another
  account ran: the `lastScheduledFor` of entries of the same `(id, createdAt)` and cron that are switched on. The widget
  never writes `lastRunAt`, `lastScheduledFor`, `runRetries` or `recordedSkips`.
- **Point 5.** The mode and the carried flag go together and unchanged, an absent key stays absent, and `importedFrom`
  is never added. But a write that lets a run start in a higher mode than the entry it changes (with the kill switch on
  or off, or by losing `importedFrom`) needs the person's `--allow-raise` on that plan.

Point 8 is one reason a definition that goes holds the task everywhere (`definition_missing`) until a person mirrors it
again. A removal (`--claude-scheduled-remove`) removes list entries only, never the definition.

## Not decided from the code

- Whether the page's task editor has a mode control, and whether the page passes the stored mode unchanged to
  `startSession`. The page is remote.
- What the CLI does with `CLAUDE_CODE_LAUNCHED_BY_SESSION_IDS`, including `"unknown"`.
- Which part reads `notifySessionId` for completion notices of Code tasks.
- The value served for gate 396417011. It decides whether carried modes survive at run start.
- When the importer (BhcPzNzB) runs and which source folders it takes. It was seen only in passing.
- The flag `a` in the page's delete handler.
