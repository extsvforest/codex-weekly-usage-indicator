# Claude 5.11492 deletion-protection review · 2026-11-14

Claude Desktop 5.11492 read as `Unreviewed`: its main process is split into
chunks, so the flows the guard follows no longer sat in one file.

- [author-evidence.md](author-evidence.md): the snippets and byte offsets the
  guard update rests on (properties a to e of the session-list invariant).
- [independent-review.md](independent-review.md): a second review by another
  model (Codex), which replayed the app's own functions and the record cache
  on a real NTFS volume. In Korean, as written.

## What the second review changed

| Finding | Answer |
|---|---|
| A record that reads but does not parse is recovered by a text scan; one empty or cut before its ids claims nothing, so a delete elsewhere can take its conversation | The app's behaviour. A list whose record has not parsed for two minutes is now reported (footer and tray). None existed on the reviewed PC |
| Copies drop `overflowCliSessionIds` | The app fills that field past 1,700 earlier transcripts (the longest lineage on the reviewed PC had 7). Such a session is now left out, and a record holding the field is not advanced |
| A later `exports.f=` kept the reviewed function in place while the name called another, and still read `Reviewed` | A shared name resolves only while its export is set once, by assignment, and the import is bound once and never assigned to |
| A kept read of a record answered for a different file with the same size and times, through both plans and the write | The plan that is published reads every record from its file; kept reads are not taken within two seconds of the write time or after ten minutes |

Left as it is, and recorded in `AGENTS.md`: the app's worktree bring-home on
resume and an import's resume confirmation move transcripts without the reap
checks. Neither deletes a transcript another account's record claims.
