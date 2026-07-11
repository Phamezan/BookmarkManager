#!/bin/bash
set -euo pipefail

# Always operate from the repo root, no matter which subfolder we're called from
REPO_ROOT=$(git rev-parse --show-toplevel)
cd "$REPO_ROOT"

BRANCH=$(git branch --show-current)
INPUT="${1:-}"

# Prevent pushing directly from main
if [ "$BRANCH" = "main" ]; then
    echo -e "\e[31mError: You are on main. Create a feature branch first!\e[0m"
    exit 1
fi

# Check for uncommitted changes before doing anything
if ! git diff --quiet || ! git diff --cached --quiet; then
    echo -e "\e[31mError: You have uncommitted changes. Commit or stash first.\e[0m"
    exit 1
fi

# Safety check: current branch must be under feature/ for CI to trigger
if [[ "$BRANCH" != feature/* ]]; then
    echo -e "\e[33mWarning: Current branch '$BRANCH' isn't under feature/, so CI won't trigger on push.\e[0m"
    read -p "Rename it to 'feature/$BRANCH' now? [y/N] " rename_confirm
    if [[ "$rename_confirm" =~ ^[Yy]$ ]]; then
        NEW_NAME="feature/$BRANCH"
        if git rev-parse --verify --quiet "$NEW_NAME" > /dev/null; then
            echo -e "\e[31mError: Branch '$NEW_NAME' already exists. Rename manually.\e[0m"
            exit 1
        fi
        git branch -m "$NEW_NAME"
        BRANCH="$NEW_NAME"
        echo -e "\e[32mRenamed to $BRANCH.\e[0m"
    else
        echo -e "\e[31mAborting. Rename manually or re-run and confirm.\e[0m"
        exit 1
    fi
fi

# Prompt if no name given for the next branch
if [ -z "$INPUT" ]; then
    read -p "Name for new feature branch: " INPUT
fi
if [ -z "$INPUT" ]; then
    echo -e "\e[31mError: No branch name given.\e[0m"
    exit 1
fi

# Ensure the feature/ prefix on the new branch, without double-prefixing
if [[ "$INPUT" == feature/* ]]; then
    NEW_BRANCH="$INPUT"
else
    NEW_BRANCH="feature/$INPUT"
fi

# Bail if that branch name is already taken
if git rev-parse --verify --quiet "$NEW_BRANCH" > /dev/null; then
    echo -e "\e[31mError: Branch '$NEW_BRANCH' already exists.\e[0m"
    exit 1
fi

# Push current feature branch, bail loudly if it fails
if ! git push -u origin "$BRANCH"; then
    echo -e "\e[31mError: Push failed. Staying on $BRANCH.\e[0m"
    exit 1
fi

# Create and switch to the new branch, off of the branch we just pushed
git checkout -b "$NEW_BRANCH"

# Clean up branches GitHub has already deleted (never touches BRANCH or NEW_BRANCH)
git fetch --prune
gone_branches=$(git branch -vv | grep ': gone]' | awk '{print $1}' | grep -vx "$BRANCH" | grep -vx "$NEW_BRANCH" || true)

if [ -n "$gone_branches" ]; then
    echo -e "\e[33mThe following local branches are gone from remote:\e[0m"
    echo "$gone_branches"
    read -p "Delete them locally? [y/N] " confirm
    if [[ "$confirm" =~ ^[Yy]$ ]]; then
        echo "$gone_branches" | xargs -r git branch -D
    fi
fi

echo -e "\e[32m🚀 Pushed $BRANCH. Now on new branch $NEW_BRANCH.\e[0m"