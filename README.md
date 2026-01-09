# GraphPaper

A GraphRAG (Graph-based Retrieval-Augmented Generation) application built on .NET 8 that transforms unstructured research papers into navigable knowledge graphs.

## Overview

GraphPaper addresses the limitations of traditional RAG systems by building structured knowledge representations from research documents. Instead of simple text fragment retrieval, it creates an interconnected web of concepts, entities, and relationships that enables comprehensive understanding and discovery.

## Core Objectives

### Knowledge Extraction
Uses Large Language Models (LLMs) to analyze research PDFs, automatically identifying:
- Key entities (concepts, authors, methodologies, findings)
- Semantic relationships between identified entities
- Contextual information and supporting evidence

### Structured Discovery
Builds a comprehensive knowledge graph in PostgreSQL that enables:
- Global question answering across entire documents
- Relationship traversal and concept mapping
- Complex queries that span multiple document sections
- Semantic search combined with structured data retrieval

### NotebookLM-like Experience
Provides an advanced chat interface featuring:
- Grounded AI responses with verifiable citations
- Direct links to source document chunks
- Context-aware conversation that maintains document relevance
- Evidence-backed answers for research integrity

## Architecture

The application follows Clean Architecture principles with clear separation of concerns:

### Domain Layer
- Entity definitions and business rules
- Database context and entity relationships
- Core domain logic independent of external dependencies

### Infrastructure Layer
- Repository pattern implementation
- Database access and data persistence
- External service integrations
- Cross-cutting concerns (logging, caching, etc.)

### Application Layer
- Use case implementations
- Business logic orchestration
- API contracts and DTOs
- Service interfaces

### API Layer
- RESTful API endpoints
- Authentication and authorization
- Request/response handling
- Dependency injection configuration

## Technology Stack

### Backend Framework
- .NET 8 with minimal APIs
- Entity Framework Core for database operations
- Clean Architecture pattern implementation

### Database
- **PostgreSQL** as the primary database
- **pgvector** extension for AI embedding storage and similarity search
- Relational graph model for entities and relationships
- Code-first migrations for database schema management

### AI Integration
- **Microsoft Semantic Kernel** for LLM orchestration
- Support for multiple AI providers (Gemini, GPT-4, etc.)
- Vector embeddings for semantic text search
- Prompt engineering and response processing

### Additional Technologies
- JWT authentication for secure API access
- Docker containerization for deployment
- MinIO for file storage (optional)
- Redis for caching (configurable)

## Getting Started

### Prerequisites
- .NET 8 SDK
- Docker and Docker Compose
- PostgreSQL with pgvector extension

### Quick Start with Docker

1. Clone the repository:
```bash
git clone https://github.com/Ch1mpleo/GraphPaper.git
cd GraphPaper
```

2. Configure environment variables in `docker-compose.yml`:
```yaml
environment:
  - GEMINI_API_KEY=your_api_key_here
  - JWT__SecretKey=your_secret_key_here
```

3. Start the application:
```bash
docker-compose up -d
```

4. Access the API at `http://localhost:5000`

### Local Development

1. Set up PostgreSQL with pgvector:
```sql
CREATE EXTENSION vector;
```

2. Update connection string in `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=graphpaper_db;Username=postgres;Password=your_password"
  }
}
```

3. Run database migrations:
```bash
dotnet ef database update --project GraphPaper.Domain --startup-project GraphPaper.API
```

4. Start the application:
```bash
dotnet run --project GraphPaper.API
```

## API Documentation

Once running, access the Swagger documentation at:
- Local: `http://localhost:5000/swagger`
- Docker: `http://localhost:5000/swagger`

## Key Features

### Document Processing
- PDF upload and text extraction
- Automated chunking for optimal processing
- Metadata extraction and indexing

### Knowledge Graph Construction
- Entity extraction using LLMs
- Relationship identification and classification
- Graph validation and consistency checking

### Intelligent Querying
- Natural language question processing
- Graph traversal for comprehensive answers
- Citation generation with source tracking

### User Management
- JWT-based authentication
- User-specific document collections
- Session management for chat interactions

## Database Schema

### Core Entities
- **Users**: User accounts and authentication
- **Documents**: Uploaded research papers and metadata
- **DocumentChunks**: Text segments with embeddings
- **ExtractedEntities**: Identified concepts and entities
- **ExtractedRelationships**: Connections between entities
- **ChatSessions**: User conversation contexts
- **ChatMessages**: Individual chat interactions
- **MessageCitations**: Links between responses and source material

## Configuration

### Required Environment Variables
```bash
# Database
ConnectionStrings__DefaultConnection=your_postgresql_connection

# AI Configuration
GEMINI_API_KEY=your_gemini_api_key

# Authentication
JWT__SecretKey=your_jwt_secret
JWT__Issuer=your_jwt_issuer
JWT__Audience=your_jwt_audience

# Optional: File Storage
MINIO_ENDPOINT=your_minio_endpoint
MINIO_ACCESS_KEY=your_access_key
MINIO_SECRET_KEY=your_secret_key
```

## Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/your-feature`
3. Commit your changes: `git commit -am 'Add some feature'`
4. Push to the branch: `git push origin feature/your-feature`
5. Submit a pull request

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Support

For questions, issues, or contributions, please visit the [GitHub repository](https://github.com/Ch1mpleo/GraphPaper) or open an issue.