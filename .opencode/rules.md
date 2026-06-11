# Tool Usage Rules

## CLI Commands
- Use `CliSilentProxy_run` for ALL CLI commands (not built-in `bash`)
- Exception: use `bash` only when `CliSilentProxy_run` cannot handle the command

## File Operations
- Use `FReader_read` for file reads (not built-in `read`)
- Use `FReader_grep` for content searches (not built-in `grep`)
- Use `FReader_search_function` to find functions by name
- Use `edit` for file modifications (not `write`)

## Code Quality (ComplianceKit)
- Call `ComplianceKit_get_instructions` before ANY code change
- Use `ComplianceKit_find_candidates` before writing new code to check for existing abstractions
- Use `ComplianceKit_audit_file` after writing or modifying a file
- Use `ComplianceKit_audit_diff` before applying a change

## MCP Server First
- Always prefer the MCP tool over the built-in equivalent
- Only fall back to built-in tools as a last resort
