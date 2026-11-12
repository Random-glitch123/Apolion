# Next Release : 1.0.1

See `Build.md` for all build tools and how-to instructions.

## Problems to fix (ordered by importance)

Standard Chrome download is overlayed over Apolion

Instagram doesn't want show video. Image somewhere in gallery

Look deeper into performance issues

---

All code health issues have been addressed. See git history for details.

---

## Git Setup & Workflow (for this project)

### Already Done ✅
```bash
cd C:\Users\Skulzd\Desktop\Apolion
git init
git config user.name "Random_glitch123"
git config user.email "Zdenekskula11@gmail.com"
git config init.defaultBranch main
git branch -m main
# Created .gitignore (excludes bin/, obj/, artifacts/, .vscode/, secrets)
git add .
git commit -m "Initial commit: Apolion browser with consolidated updater"
```

### Push to GitHub (one-time setup)

**GitHub requires a Personal Access Token (PAT) instead of password for git operations.**

1. **Create PAT on GitHub:**
   - Go to: https://github.com/settings/tokens
   - Click "Generate new token (classic)"
   - Name: "Apolion repo"
   - Expiration: 90 days (or your preference)
   - Scopes: check **`repo`** (full control of private repos)
   - Click "Generate token" → **COPY IT NOW** (you won't see it again)

2. **Create the remote repo on GitHub:**
   - Go to: https://github.com/new
   - Repository name: `Apolion`
   - Owner: `Random_glitch123`
   - Public or Private (your choice)
   - **DO NOT** initialize with README, .gitignore, or license (we already have them)
   - Click "Create repository"

3. **Push from local:**
   ```bash
   cd C:\Users\Skulzd\Desktop\Apolion
   git remote add origin https://github.com/Random_glitch123/Apolion.git
   git push -u origin main
   ```
   - When prompted for username: `Random_glitch123`
   - When prompted for password: **PASTE THE PAT** (not your GitHub password)

### Daily Workflow

```bash
# Check status
git status

# See changes
git diff              # unstaged
git diff --staged     # staged

# Stage & commit
git add <file>        # specific file
git add -A            # everything
git commit -m "Fix: describe what changed"

# View history
git log --oneline -10
git lg                # if alias set (see below)

# Branching
git switch -c fix/issue-name    # create & switch to new branch
# ... work & commit ...
git switch main                 # back to main
git merge fix/issue-name        # merge feature
git branch -d fix/issue-name    # delete branch (after merge)

# Sync with remote
git push              # send commits
git pull              # get latest (merges by default)
```

### Useful Aliases (run once)

```bash
git config --global alias.st status
git config --global alias.co checkout
git config --global alias.br branch
git config --global alias.lg "log --oneline --graph --all -20"
git config --global alias.undo "reset --soft HEAD~1"  # undo last commit, keep changes staged
```

Then use: `git st`, `git lg`, `git undo`, etc.

### Common Fixes

| Problem | Fix |
|---------|-----|
| Wrong commit message | `git commit --amend -m "Correct message"` |
| Forgot to add file | `git add forgotten.cs && git commit --amend --no-edit` |
| Discard changes to file | `git restore <file>` |
| Discard ALL uncommitted | `git restore .` ⚠️ |
| Unstage file | `git restore --staged <file>` |
| Commit to wrong branch | `git switch correct-branch && git cherry-pick <hash>` |

### Visual Tools (optional)
- **GitHub Desktop** — simple GUI
- **VS Code** — built-in Source Control tab (Ctrl+Shift+G)
- **Fork / GitKraken** — advanced GUIs