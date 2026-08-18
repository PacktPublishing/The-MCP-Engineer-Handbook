"""Stores previewed plans keyed by token — the explicit handle pattern from
Chapter 4 applied to preview-before-execute. This sample keeps the dictionary
in process memory; in production it belongs in the durable storage the server
already uses (a database, Redis, ...) so that execute_find_and_replace can
land on any server instance. The token, not the connection, carries the state.
A production token would also bind the authenticated user, so a preview cannot
be redeemed by someone else."""

import secrets
import threading
import time
from enum import Enum

from find_replace_planner import FindReplacePlan

TIME_TO_LIVE_SECONDS = 5 * 60


class RedeemResult(Enum):
    NOT_FOUND = "not_found"
    EXPIRED = "expired"
    VALID = "valid"


class PreviewStore:
    def __init__(self) -> None:
        self._gate = threading.Lock()
        self._previews: dict[str, tuple[FindReplacePlan, float]] = {}

    def add(self, plan: FindReplacePlan) -> str:
        token = secrets.token_hex(6)
        with self._gate:
            self._previews[token] = (plan, time.monotonic() + TIME_TO_LIVE_SECONDS)
        return token

    def redeem(self, token: str) -> tuple[RedeemResult, FindReplacePlan | None]:
        """Tokens are single-use: redeeming removes the entry whether or not
        the subsequent execution succeeds. A failed execution means the state
        changed, so a fresh preview is needed anyway."""
        with self._gate:
            stored = self._previews.pop(token, None)
        if stored is None:
            return RedeemResult.NOT_FOUND, None
        plan, expires_at = stored
        if expires_at < time.monotonic():
            return RedeemResult.EXPIRED, None
        return RedeemResult.VALID, plan
