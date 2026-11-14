# Adopting a project that already has Docker files

Added in 4.10.0. Smart projects no longer assume ShipRight creates the Docker setup. "Adopt Docker setup" (Projects page) reads
what is already there and creates the project from it.

## What it understands
- **Dockerfiles with any name**: `Dockerfile`, `Dockerfile.api`, `backend.Dockerfile`, several in one folder, nested folders.
  `node_modules`, `.git`, `bin`, `obj`, `.next`, `dist` and similar are never searched.
- **The compose file**, read without a YAML library (each service's `image`, `build` and `depends_on`). A compose `build:` section is
  the source of truth for that service; otherwise Dockerfiles are matched to services by name (stem, folder, image name), with ties
  reported and never guessed. Services no Dockerfile fits (databases, proxies) are listed as "left alone".
- **The build context**, from what the Dockerfile copies: the nearest folder (from the Dockerfile's own folder upward) where every
  `COPY` source exists. For `COPY back-end/x ...` in a root-level Dockerfile that is the repository root.
- **Stages and arguments**: extra end stages are offered as optional images later; a build `ARG` with no default and no value is
  flagged, because an image built without it is wrong without failing.
- **Version files**: found next to the app the service builds; otherwise a place to create one is suggested (create-only, `0.1.0`).
- **The shared tag variable**: services that share one tag variable (for example `IMAGE_TAG`) are reported, since they cannot be
  versioned independently.

Verified against `savanna-herd-iq`: backend and frontend found with their root `backend.Dockerfile`/`frontend.Dockerfile` and the
repository root as context, the four infrastructure services left alone, `back-end/dockerfile` and the starter-pack copies offered as
alternatives, the shared `IMAGE_TAG` and the front end's `NEXT_PUBLIC_API_URL` argument flagged.

## What it writes (only after the person agrees, never silently)
- **Version files**: create-only.
- **The compose file**: each built service gets its own tag variable on its image line (`${BACKEND_TAG:-repo:latest}`), keeping the
  current default tag; for build-on-server a `build:` section with the recipe is added too. Every other line is byte for byte as it was.
  The changes are listed first, the box is ticked by the person, and the original is kept as `<file>.shipright-backup`
  (numbered if one already exists). Without this the deploy would not update images, because ShipRight exports one variable per service.
- The project itself, with each service's Dockerfile path, build stage and arguments (the **build recipe**).

## The build recipe
`DockerfilePath` (relative to the build context, or absolute), `BuildTarget` and `BuildArgs` on a service. Empty means exactly the old
command. It is added to `docker build` by a wrapper around the process runner (`BuildRecipeProcessRunner`) and survives saves from the
original wizard through a wrapper around the project store; both are wired in by `DecorateSingleton`, leaving the existing
registrations and build code untouched. An invalid recipe, or two projects building one image with different recipes, stops the build
with a plain message. Verified with a real docker build (custom file name, `--target`, `--build-arg`).

## Endpoints (Admin)
`POST /api/setup/adopt/plan`, `/compose-preview`, `/compose-apply`, `/version-files`. The client only names the folder, the mode
(`registry` or `build-on-server`) and its Dockerfile choices; paths and file contents are always computed on the server.

## Not done
- Per-extra-stage images (a second image from another stage of one Dockerfile) are reported but not yet created as services.
- Rebuilding only what changed in build-on-server deploys (every deploy still builds all services).
- Sending the code from this computer instead of a git host.
