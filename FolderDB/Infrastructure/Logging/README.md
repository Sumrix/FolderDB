## What is this?

This folder is a small adapter layer over `Microsoft.Extensions.Logging` that automatically adds the collection name to logger scopes. It exists to keep collection-aware logging consistent without repeating manual `BeginScope` plumbing throughout the codebase.
