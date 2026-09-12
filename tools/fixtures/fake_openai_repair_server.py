#!/usr/bin/env python3
"""Loopback-only OpenAI-compatible fixture for the bounded writer repair probe."""

from __future__ import annotations

import argparse
import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


MEALS = [
    "Porridge aux pommes",
    "Tartine de pois chiches",
    "Yaourt aux poires",
    "Omelette aux herbes",
    "Salade de lentilles",
    "Riz aux légumes",
    "Soupe de haricots",
    "Pâtes aux épinards",
    "Compote de prunes",
    "Noix et abricots",
    "Galette d'avoine",
    "Concombre au fromage blanc",
    "Gratin de courgettes",
    "Curry de pois cassés",
    "Polenta aux champignons",
    "Poivrons farcis",
    "Semoule aux carottes",
    "Quiche aux poireaux",
    "Dahl de lentilles corail",
    "Orge aux légumes",
]


def repaired_writer_payload() -> str:
    days = ["Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi"]
    headers = ["Petit-déjeuner", "Déjeuner", "Collation", "Souper"]
    rows = []
    claims = []
    for day_index, day in enumerate(days):
        cells = []
        for column_index, _ in enumerate(headers):
            index = day_index * len(headers) + column_index
            claim_id = f"C{index + 1:02d}"
            evidence_id = f"E{index + 1:02d}"
            cells.append(f"{MEALS[index]} [{claim_id}]")
            claims.append(
                {
                    "claimId": claim_id,
                    "text": MEALS[index],
                    "evidenceIds": [evidence_id],
                }
            )
        rows.append("| " + day + " | " + " | ".join(cells) + " |")
    answer = "\n".join(
        [
            "| Jour | " + " | ".join(headers) + " |",
            "|---|---|---|---|---|",
            *rows,
        ]
    )
    return json.dumps(
        {"outcome": "answered", "answerText": answer, "claims": claims},
        ensure_ascii=False,
        separators=(",", ":"),
    )


class FixtureState:
    def __init__(self, artifact_directory: Path) -> None:
        self.artifact_directory = artifact_directory
        self.lock = threading.Lock()
        self.call_count = 0
        self.trace: list[dict[str, object]] = []

    def next_response(self, request_path: str, request_bytes: int) -> str:
        with self.lock:
            self.call_count += 1
            call = self.call_count
            role = {1: "planner", 2: "writer-malformed", 3: "writer-repair"}.get(
                call, "unexpected"
            )
            self.trace.append(
                {
                    "call": call,
                    "role": role,
                    "path": request_path,
                    "requestBytes": request_bytes,
                }
            )
            self.artifact_directory.mkdir(parents=True, exist_ok=True)
            (self.artifact_directory / "request-trace.json").write_text(
                json.dumps(self.trace, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )

        if call == 1:
            return '{"queries":[{"query":"préparations documentées","topK":20}]}'
        if call == 2:
            return '{"outcome":"answered","answerText":"Réponse incomplète"'
        if call == 3:
            return repaired_writer_payload()
        return '{"error":"unexpected_fixture_call"}'


class FixtureHandler(BaseHTTPRequestHandler):
    server_version = "SAAIARepairFixture/1.0"

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/health":
            self._json_response(200, {"status": "ok"})
            return
        self._json_response(404, {"error": "not_found"})

    def do_POST(self) -> None:  # noqa: N802
        if self.path != "/v1/chat/completions":
            self._json_response(404, {"error": "not_found"})
            return
        content_length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(content_length)
        try:
            json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            self._json_response(400, {"error": "invalid_json"})
            return
        content = self.server.state.next_response(self.path, len(raw))  # type: ignore[attr-defined]
        self._json_response(
            200,
            {
                "id": "saaia-local-repair-fixture",
                "object": "chat.completion",
                "created": 0,
                "model": "saaia-local-repair-fixture-v1",
                "choices": [
                    {
                        "index": 0,
                        "message": {"role": "assistant", "content": content},
                        "finish_reason": "stop",
                    }
                ],
                "usage": {
                    "prompt_tokens": 100,
                    "completion_tokens": 100,
                    "total_tokens": 200,
                },
            },
        )

    def log_message(self, format: str, *args: object) -> None:
        return

    def _json_response(self, status: int, payload: dict[str, object]) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--artifact-directory", type=Path, required=True)
    args = parser.parse_args()
    state = FixtureState(args.artifact_directory.resolve())
    server = ThreadingHTTPServer(("127.0.0.1", args.port), FixtureHandler)
    server.state = state  # type: ignore[attr-defined]
    server.serve_forever()


if __name__ == "__main__":
    main()
