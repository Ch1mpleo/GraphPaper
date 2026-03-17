# Copilot instructions

## Language Policy

All instructions and prompts in this repository must be written in English. This applies to:
- All rule and instruction files in `.github/instructions/`
- All documentation and code comments intended for contributors

## Development code generation

When working with C# code, follow these instructions very carefully.

It is **EXTREMELY important that you follow the instructions in the rule files very carefully.**

### Workflow implementation

**IMPORTANT:** Always follow these steps when implementing new features:

1. Consult any relevant instructions files listed below and start by listing which rule files have been used to guide the implementation (e.g. `Instructions used: [minimal-api.instructions.md, domain-driven-design.instructions.md]`).

2. Always run `dotnet test` or `dotnet build` to verify that all tests pass before committing your changes. 
Don't ask to run the tests, just do it. If you are not sure how to run the tests, ask for help. 
You can also use `dotnet watch test` to run the tests automatically when you change the code.

3. Prefer strict Clean Architecture dependency flow: API -> Application -> Domain, and Infrastructure should only implement interfaces (no Application -> Infrastructure reference). 

4. Do not use `IClaimsService` in the controller layer; avoid claim-service dependency at the controller stage.

5. Don't create unnecessary md files after implementing a feature.

6. Fix any compiler warnings and errors before going to the next step.

### Document Parsing Strategy

- Prefer a single document parsing strategy (Docling or PdfPig/OpenXml) or introduce an explicit router; avoid registering Docling without using it in the processing pipeline.

## GPU Usage

For optimal performance, use the GPU version of `docling-serve` with an NVIDIA RTX 5060 Ti 16GB GPU. Ensure that the parser setup is GPU-only, with no CPU fallback. 

## Docker Configuration

- Make direct changes in `docker-compose.yml` for any necessary configurations.