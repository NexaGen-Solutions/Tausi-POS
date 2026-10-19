# Decisions

Each entry: date, decision, reason. Newest at the bottom of each section.

## Stack

| Date | Decision | Reason |
| --- | --- | --- |
| 2026-10-19 | Server on .NET 10 (LTS), ASP.NET Core minimal APIs, SignalR, EF Core 10. | Set in AGENTS.md. |
| 2026-10-19 | PostgreSQL 16 for Linux and development, SQL Server 2022 Express for Windows customers, one EF Core model. | Set in AGENTS.md. |
| 2026-10-19 | Web UI: React 19 + TypeScript + Vite, React Router, TanStack Query, our own components. | Set in AGENTS.md. |
| 2026-10-19 | Terminal: Tauri v2 with the store, autostart and updater plugins. | Set in AGENTS.md. |
| 2026-10-19 | Tests: xUnit + FluentAssertions **7.x** (server), Testcontainers PostgreSQL (integration), Vitest + Testing Library (web). | FluentAssertions 8 and later need a paid licence for commercial use; 7.x is Apache-2.0. |
| 2026-10-19 | TypeScript pinned to 6.0, ESLint (flat config) + typescript-eslint, Prettier. | The Vite template now uses TypeScript 7 and oxlint; the task asks for ESLint, and typescript-eslint supports TypeScript below 6.1. |

## Follow-ups
