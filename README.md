# C# Console Runner for Exams

A deliberately minimal C# console runner intended for assessment environments where students need to test code without IDE autocomplete, IntelliSense, AI assistance, or detailed debugging feedback.

## What students see

- A plain C# text area
- **Run** and **Reset**
- Successful `Console.WriteLine(...)` output
- Generic failure messages only

The browser never receives compiler diagnostics, error codes, line numbers, stack traces, or exception messages.

## Architecture

- `frontend/` — static HTML/CSS/JS deployed to GitHub Pages
- `backend/Runner.Api/` — ASP.NET Core .NET 10 API
- Student code is compiled in a disposable Docker SDK container
- If compilation succeeds, the assembly runs in a separate restricted .NET runtime container

Runtime containers use:

- no network
- read-only root filesystem
- read-only program mount
- dropped Linux capabilities
- `no-new-privileges`
- memory, CPU, PID, and wall-clock limits
- output truncation

> Important: GitHub Pages can host only the frontend. The Runner API must be hosted on a Linux machine/VM that has Docker Engine and .NET 10 installed. This is required because arbitrary C# execution must happen inside an isolated runtime rather than on GitHub Pages.

## 1. Upload this repository to GitHub

Create a repository and upload the entire project. Use `main` as the default branch.

## 2. Deploy the Runner API

Use a Linux VM that you control. Install:

- Docker Engine
- .NET 10 ASP.NET Core runtime (or SDK for publishing on the server)
- optionally Nginx + HTTPS

Pre-pull the two runtime images:

```bash
docker pull mcr.microsoft.com/dotnet/sdk:10.0
docker pull mcr.microsoft.com/dotnet/runtime:10.0
```

Publish the API:

```bash
dotnet publish backend/Runner.Api/Runner.Api.csproj -c Release -o publish/backend
```

Copy `publish/backend` to `/opt/csharp-runner` on the server.

Create the working directory:

```bash
sudo mkdir -p /opt/csharp-runner/runner-work
```

The account running the API must be allowed to invoke Docker. Treat Docker access as privileged server access; use a dedicated VM for this service.

Before production use, edit `backend/Runner.Api/appsettings.Production.json` and replace the example GitHub Pages origin with your real origin, for example:

```json
{
  "AllowedOrigins": [
    "https://yourname.github.io"
  ]
}
```

A sample systemd unit is in `deploy/csharp-runner.service`. A sample reverse-proxy configuration is in `deploy/nginx.conf.example`.

The API should ultimately be reachable at an HTTPS URL such as:

```text
https://runner.example.edu
```

Health check:

```text
GET https://runner.example.edu/health
```

## 3. Configure GitHub Pages

In the GitHub repository:

1. Open **Settings → Pages**.
2. Under **Build and deployment**, choose **GitHub Actions**.
3. Open **Settings → Secrets and variables → Actions → Variables**.
4. Add repository variable:

```text
RUNNER_API_URL=https://runner.example.edu
```

5. Push to `main`, or run **Deploy Frontend to GitHub Pages** manually from Actions.

The included `.github/workflows/deploy-pages.yml` publishes only the `frontend/` directory and injects the API URL during deployment.

## Student-visible result policy

The API returns only these statuses:

- `success` — normal console stdout is returned
- `compile_error` — no compiler diagnostics are returned
- `runtime_error` — no exception or stack trace is returned
- `timeout` — generic time-limit message
- `invalid` — invalid/oversized request

Examples:

### Successful program

```csharp
using System;
class Program
{
    static void Main()
    {
        Console.WriteLine(2 + 3);
    }
}
```

Student sees:

```text
5
```

### Compiler error

```csharp
using System;
class Program
{
    static void Main()
    {
        Console.WriteLine("Hello")
    }
}
```

Student sees only:

```text
Compilation failed.

Review your code and try again.
```

### Runtime exception

```csharp
using System;
class Program
{
    static void Main()
    {
        Console.WriteLine(10 / int.Parse("0"));
    }
}
```

Student sees only:

```text
Program terminated with an error.
```

## Security notes

This project is intentionally designed so student code does **not** execute inside the ASP.NET Core API process. Each compile/run uses a separate Docker container.

For an internet-facing deployment:

- use a dedicated VM for the runner
- keep Docker and the OS patched
- keep the API behind HTTPS
- restrict CORS to the actual GitHub Pages origin
- add firewall/rate-limiting rules at the reverse proxy or cloud edge
- do not mount secrets or host directories into runner containers
- do not put the API on the same host as sensitive systems
- monitor disk usage and Docker daemon health

The included API also caps source length, rate-limits requests, truncates stdout, and deletes temporary work directories after each request.

## Respondus / LockDown Browser note

The frontend has no external libraries, popups, documentation links, autocomplete, or AI calls. Before using it in a graded assessment, test the final GitHub Pages URL and Runner API URL inside the exact LockDown Browser configuration used by your institution, because allowed-domain/network policies are controlled by that environment.

## Optional: run the API itself with Docker Compose

A `compose.yml` and API `Dockerfile` are included. On a dedicated Linux VM:

```bash
sudo mkdir -p /opt/csharp-runner/runner-work
sudo docker compose up -d --build
```

This maps the host Docker socket into the API container because the API must create the short-lived sandbox containers. Docker-socket access is highly privileged, so use this only on a dedicated runner host, not on a server that contains unrelated sensitive workloads.
