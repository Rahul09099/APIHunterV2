# Requirements Document: Server and Credential Detection

## Introduction

This feature expands APIHunterV2 beyond API keys to detect and verify unsecured server credentials found in code repositories. The system will detect SSH credentials, FTP/SFTP connections, RDP and remote access credentials, SMTP and email server credentials, cPanel and hosting control panel credentials, web server authentication, database connection strings, cloud metadata endpoints, and container orchestration credentials. It includes multi-stage verification with network checks, authentication tests, and advanced search techniques including OSINT integration. The implementation is optimized for Render free tier deployment constraints with memory-efficient processing and resource limitations.

## Glossary

- **Server_Credential_Provider**: The provider class responsible for detecting and verifying server credentials
- **SSH_Credential**: Secure Shell authentication credential including username, host, and private key or password
- **FTP_Credential**: File Transfer Protocol credential including username, password, and host
- **Database_Connection_String**: Connection string containing database credentials for MySQL, PostgreSQL, MongoDB, Redis, etc.
- **Cloud_Metadata_Endpoint**: Cloud provider metadata service endpoint that may expose credentials or sensitive information
- **RDP_Credential**: Remote Desktop Protocol credential including username, password, host, and domain
- **VNC_Credential**: Virtual Network Computing credential including username, password, and host
- **WinRM_Credential**: Windows Remote Management credential including username, password, and endpoint
- **SMTP_Credential**: Simple Mail Transfer Protocol credential including username, password, host, and port
- **IMAP_Credential**: Internet Message Access Protocol credential including username, password, and host
- **POP3_Credential**: Post Office Protocol credential including username, password, and host
- **cPanel_Credential**: cPanel control panel credential including username, password, and cPanel URL
- **WHM_Credential**: Web Host Manager credential including root username, password, and WHM URL
- **Plesk_Credential**: Plesk control panel credential including admin username, password, and Plesk URL
- **Network_Verifier**: Service that performs TCP connectivity tests and port scanning
- **Authentication_Verifier**: Service that performs safe authentication tests without causing lockouts
- **OSINT_Service**: Open Source Intelligence service integrating with Shodan, Censys, and GreyNoise
- **Entropy_Analyzer**: Service that calculates Shannon entropy to identify high-randomness passwords
- **Context_Extractor**: Service that extracts surrounding code context to find related credentials
- **Geolocation_Service**: Service that identifies country, city, and ISP for discovered IP addresses
- **Honeypot_Detector**: Service that identifies security traps to avoid false positives
- **Source_Map_Parser**: Service that parses JavaScript source maps to find original unminified code
- **SQL_Dump_Scanner**: Service that scans database dump files for embedded credentials
- **Render_Optimizer**: Service that applies memory and resource optimizations for Render free tier deployment
- **Direct_IO_Manager**: Service that manages file I/O operations with memory constraints and automatically selects optimal I/O strategy
- **Pattern_Compiler**: Service that compiles and manages regex patterns for credential detection
- **Verification_Queue**: Queue system for managing credential verification with rate limiting

## Requirements

### Requirement 1: Detect SSH Credentials

**User Story:** As a security researcher, I want to detect exposed SSH credentials in GitHub repositories, so that I can identify compromised server access.

#### Acceptance Criteria

1. WHEN a GitHub file contains an SSH connection pattern (ssh user@host), THE Server_Credential_Provider SHALL extract the username and host
2. WHEN a GitHub file contains an SSH private key header (-----BEGIN RSA PRIVATE KEY-----), THE Server_Credential_Provider SHALL extract the private key content
3. WHEN a GitHub file contains an SSH private key header (-----BEGIN OPENSSH PRIVATE KEY-----), THE Server_Credential_Provider SHALL extract the OpenSSH private key content
4. WHEN a GitHub file contains an SSH private key header (-----BEGIN DSA PRIVATE KEY-----), THE Server_Credential_Provider SHALL extract the DSA private key content
5. WHEN an SSH connection pattern is found, THE Context_Extractor SHALL search within 10 lines for password or private key information
6. THE Server_Credential_Provider SHALL detect SSH config file patterns (~/.ssh/config format)
7. THE Server_Credential_Provider SHALL detect SSH authorized_keys file patterns
8. THE Server_Credential_Provider SHALL use regex patterns compatible with the existing provider pattern system

### Requirement 2: Detect FTP and SFTP Credentials

**User Story:** As a security researcher, I want to detect exposed FTP/SFTP credentials in GitHub repositories, so that I can identify compromised file server access.

#### Acceptance Criteria

1. WHEN a GitHub file contains an FTP URL pattern (ftp://user:pass@host), THE Server_Credential_Provider SHALL extract the username, password, and host
2. WHEN a GitHub file contains an SFTP URL pattern (sftp://user:pass@host), THE Server_Credential_Provider SHALL extract the username, password, and host
3. WHEN a GitHub file contains an FTPS URL pattern (ftps://user:pass@host), THE Server_Credential_Provider SHALL extract the username, password, and host
4. WHEN FTP credentials are found without explicit protocol, THE Server_Credential_Provider SHALL detect based on port 21 context
5. WHEN SFTP credentials are found without explicit protocol, THE Server_Credential_Provider SHALL detect based on port 22 context
6. THE Server_Credential_Provider SHALL detect FileZilla configuration file patterns
7. THE Server_Credential_Provider SHALL detect WinSCP configuration file patterns
8. THE Context_Extractor SHALL search within 10 lines for related FTP configuration parameters

### Requirement 3: Detect Database Connection Strings

**User Story:** As a security researcher, I want to detect exposed database connection strings in GitHub repositories, so that I can identify compromised database access.

#### Acceptance Criteria

1. WHEN a GitHub file contains a MySQL connection string pattern, THE Server_Credential_Provider SHALL extract the username, password, host, and database name
2. WHEN a GitHub file contains a PostgreSQL connection string pattern, THE Server_Credential_Provider SHALL extract the username, password, host, and database name
3. WHEN a GitHub file contains a MongoDB connection string pattern, THE Server_Credential_Provider SHALL extract the username, password, host, and database name
4. WHEN a GitHub file contains a Redis connection string pattern (redis://[user:pass@]host:port), THE Server_Credential_Provider SHALL extract the credentials and host
5. WHEN a GitHub file contains an ElasticSearch connection string pattern, THE Server_Credential_Provider SHALL extract the username, password, and host
6. WHEN a GitHub file contains a RabbitMQ connection string pattern (amqp://user:pass@host:port), THE Server_Credential_Provider SHALL extract the credentials and host
7. WHEN a GitHub file contains a Microsoft SQL Server connection string, THE Server_Credential_Provider SHALL extract the username, password, and server
8. THE Server_Credential_Provider SHALL detect JDBC connection string patterns for various databases
9. THE Server_Credential_Provider SHALL detect SQLite database file paths with potential credentials in connection strings

### Requirement 4: Detect RDP and Remote Access Credentials

**User Story:** As a security researcher, I want to detect exposed RDP and remote access credentials in GitHub repositories, so that I can identify compromised remote server access.

#### Acceptance Criteria

1. WHEN a GitHub file contains an RDP connection pattern (rdp://user:pass@host:port or mstsc /v:host), THE Server_Credential_Provider SHALL extract the username, password, and host
2. WHEN a GitHub file contains Windows RDP configuration files (.rdp files), THE Server_Credential_Provider SHALL extract connection details and credentials
3. WHEN a GitHub file contains VNC connection patterns (vnc://user:pass@host:port), THE Server_Credential_Provider SHALL extract the username, password, and host
4. WHEN a GitHub file contains TeamViewer credentials or connection IDs, THE Server_Credential_Provider SHALL extract the ID and password
5. WHEN a GitHub file contains AnyDesk connection details, THE Server_Credential_Provider SHALL extract the connection ID and password
6. WHEN a GitHub file contains Chrome Remote Desktop configuration, THE Server_Credential_Provider SHALL extract the connection details
7. THE Server_Credential_Provider SHALL detect Windows Remote Management (WinRM) credentials and endpoints
8. THE Server_Credential_Provider SHALL detect PowerShell remoting session configurations with credentials
9. WHEN RDP credentials are found, THE Context_Extractor SHALL search within 10 lines for domain information and additional authentication details

### Requirement 5: Detect Cloud and Container Credentials

**User Story:** As a security researcher, I want to detect exposed cloud and container credentials in GitHub repositories, so that I can identify compromised infrastructure access.

#### Acceptance Criteria

1. WHEN a GitHub file contains a Kubernetes service host variable (KUBERNETES_SERVICE_HOST), THE Server_Credential_Provider SHALL extract the host and search for related tokens
2. WHEN a GitHub file contains a KubeConfig file pattern, THE Server_Credential_Provider SHALL extract cluster information and credentials
3. WHEN a GitHub file contains a Docker host pattern (DOCKER_HOST=tcp://host:port), THE Server_Credential_Provider SHALL extract the host and port
4. WHEN a GitHub file contains Docker registry credentials, THE Server_Credential_Provider SHALL extract the username, password, and registry URL
5. WHEN a GitHub file contains Docker Compose environment variables, THE Server_Credential_Provider SHALL extract database and service credentials
6. THE Server_Credential_Provider SHALL detect Helm chart values containing credentials
7. THE Server_Credential_Provider SHALL detect Terraform configuration files with provider credentials
8. THE Server_Credential_Provider SHALL detect Ansible inventory files with connection credentials

### Requirement 6: Detect SMTP and Email Server Credentials

**User Story:** As a security researcher, I want to detect exposed SMTP and email server credentials in GitHub repositories, so that I can identify compromised email infrastructure access.

#### Acceptance Criteria

1. WHEN a GitHub file contains SMTP connection patterns (smtp://user:pass@host:port), THE Server_Credential_Provider SHALL extract the username, password, host, and port
2. WHEN a GitHub file contains SMTP configuration with separate host, username, and password fields, THE Server_Credential_Provider SHALL extract all credential components
3. WHEN a GitHub file contains IMAP connection patterns (imap://user:pass@host:port), THE Server_Credential_Provider SHALL extract the username, password, and host
4. WHEN a GitHub file contains POP3 connection patterns (pop3://user:pass@host:port), THE Server_Credential_Provider SHALL extract the username, password, and host
5. THE Server_Credential_Provider SHALL detect SMTP configuration in environment variables (SMTP_HOST, SMTP_USER, SMTP_PASSWORD, SMTP_PORT)
6. THE Server_Credential_Provider SHALL detect email configuration in application config files (mail.properties, email.config, .env)
7. THE Server_Credential_Provider SHALL detect SendGrid API keys and SMTP credentials
8. THE Server_Credential_Provider SHALL detect Mailgun API keys and SMTP credentials
9. THE Server_Credential_Provider SHALL detect AWS SES SMTP credentials
10. WHEN SMTP credentials are found, THE Context_Extractor SHALL search within 10 lines for TLS/SSL settings and authentication methods

### Requirement 7: Detect cPanel and Hosting Control Panel Credentials

**User Story:** As a security researcher, I want to detect exposed cPanel and hosting control panel credentials in GitHub repositories, so that I can identify compromised hosting infrastructure access.

#### Acceptance Criteria

1. WHEN a GitHub file contains cPanel login credentials, THE Server_Credential_Provider SHALL extract the username, password, and cPanel URL
2. WHEN a GitHub file contains WHM (Web Host Manager) credentials, THE Server_Credential_Provider SHALL extract the root username, password, and WHM URL
3. WHEN a GitHub file contains Plesk control panel credentials, THE Server_Credential_Provider SHALL extract the admin username, password, and Plesk URL
4. WHEN a GitHub file contains DirectAdmin credentials, THE Server_Credential_Provider SHALL extract the username, password, and DirectAdmin URL
5. THE Server_Credential_Provider SHALL detect cPanel API tokens and authentication keys
6. THE Server_Credential_Provider SHALL detect WHM API tokens and root access keys
7. THE Server_Credential_Provider SHALL detect Plesk API keys and secret keys
8. THE Server_Credential_Provider SHALL detect cPanel configuration files (.cpanel, cpanel.yml)
9. WHEN control panel credentials are found, THE Context_Extractor SHALL search within 10 lines for domain information and server details

### Requirement 8: Detect Web Server and Application Server Credentials

**User Story:** As a security researcher, I want to detect exposed web server and application server credentials in GitHub repositories, so that I can identify compromised web infrastructure access.

#### Acceptance Criteria

1. WHEN a GitHub file contains Apache HTTP server configuration with credentials, THE Server_Credential_Provider SHALL extract the username, password, and server details
2. WHEN a GitHub file contains Nginx server configuration with authentication, THE Server_Credential_Provider SHALL extract the credentials and server information
3. WHEN a GitHub file contains IIS server configuration with credentials, THE Server_Credential_Provider SHALL extract the authentication details
4. WHEN a GitHub file contains Tomcat server configuration (tomcat-users.xml), THE Server_Credential_Provider SHALL extract user credentials and roles
5. WHEN a GitHub file contains JBoss/WildFly server credentials, THE Server_Credential_Provider SHALL extract the management and application credentials
6. WHEN a GitHub file contains WebLogic server credentials, THE Server_Credential_Provider SHALL extract the administrative and deployment credentials
7. THE Server_Credential_Provider SHALL detect Jenkins server credentials and API tokens
8. THE Server_Credential_Provider SHALL detect GitLab server credentials and access tokens
9. THE Server_Credential_Provider SHALL detect Nexus repository manager credentials

### Requirement 9: Perform Network Connectivity Verification

**User Story:** As a security researcher, I want to verify network connectivity to discovered servers, so that I can confirm if the servers are accessible.

#### Acceptance Criteria

1. WHEN server credentials are discovered, THE Network_Verifier SHALL perform a TCP connection test to the specified host and port
2. WHEN SSH credentials are found, THE Network_Verifier SHALL test connectivity on port 22
3. WHEN FTP credentials are found, THE Network_Verifier SHALL test connectivity on port 21
4. WHEN SFTP credentials are found, THE Network_Verifier SHALL test connectivity on port 22
5. WHEN RDP credentials are found, THE Network_Verifier SHALL test connectivity on port 3389
6. WHEN VNC credentials are found, THE Network_Verifier SHALL test connectivity on port 5900
7. WHEN WinRM credentials are found, THE Network_Verifier SHALL test connectivity on ports 5985 (HTTP) and 5986 (HTTPS)
8. WHEN SMTP credentials are found, THE Network_Verifier SHALL test connectivity on ports 25, 465 (SMTPS), and 587 (submission)
9. WHEN IMAP credentials are found, THE Network_Verifier SHALL test connectivity on ports 143 and 993 (IMAPS)
10. WHEN POP3 credentials are found, THE Network_Verifier SHALL test connectivity on ports 110 and 995 (POP3S)
11. WHEN cPanel credentials are found, THE Network_Verifier SHALL test connectivity on ports 2082 (HTTP) and 2083 (HTTPS)
12. WHEN WHM credentials are found, THE Network_Verifier SHALL test connectivity on ports 2086 (HTTP) and 2087 (HTTPS)
13. WHEN Plesk credentials are found, THE Network_Verifier SHALL test connectivity on port 8443
14. WHEN web server credentials are found, THE Network_Verifier SHALL test connectivity on ports 80, 443, 8080, and 8443
15. WHEN database credentials are found, THE Network_Verifier SHALL test connectivity on the appropriate default port (3306 for MySQL, 5432 for PostgreSQL, 27017 for MongoDB)
16. WHEN custom ports are specified in credentials, THE Network_Verifier SHALL use the specified port for connectivity testing
17. THE Network_Verifier SHALL set a 10-second timeout for all connectivity tests
18. WHEN connectivity tests fail, THE Network_Verifier SHALL mark the credential as "Network Unreachable"
19. WHEN connectivity tests succeed, THE Network_Verifier SHALL mark the credential as "Network Accessible"

### Requirement 8: Perform Safe Authentication Testing

**User Story:** As a security researcher, I want to safely test discovered credentials without causing account lockouts, so that I can verify credential validity responsibly.

#### Acceptance Criteria

1. WHEN credentials pass network connectivity tests, THE Authentication_Verifier SHALL perform a single authentication attempt
2. WHEN SSH credentials are tested, THE Authentication_Verifier SHALL attempt SSH key-based or password authentication with immediate disconnection
3. WHEN FTP credentials are tested, THE Authentication_Verifier SHALL attempt FTP login with immediate logout
4. WHEN RDP credentials are tested, THE Authentication_Verifier SHALL attempt RDP connection negotiation without full login
5. WHEN VNC credentials are tested, THE Authentication_Verifier SHALL attempt VNC authentication handshake without screen access
6. WHEN WinRM credentials are tested, THE Authentication_Verifier SHALL attempt WinRM authentication without command execution
7. WHEN SMTP credentials are tested, THE Authentication_Verifier SHALL attempt SMTP authentication without sending emails
8. WHEN IMAP credentials are tested, THE Authentication_Verifier SHALL attempt IMAP login with immediate logout
9. WHEN POP3 credentials are tested, THE Authentication_Verifier SHALL attempt POP3 login with immediate logout
10. WHEN cPanel credentials are tested, THE Authentication_Verifier SHALL attempt cPanel API authentication without performing actions
11. WHEN WHM credentials are tested, THE Authentication_Verifier SHALL attempt WHM API authentication without performing actions
12. WHEN Plesk credentials are tested, THE Authentication_Verifier SHALL attempt Plesk API authentication without performing actions
13. WHEN web server credentials are tested, THE Authentication_Verifier SHALL attempt HTTP authentication without accessing protected resources
14. WHEN database credentials are tested, THE Authentication_Verifier SHALL attempt database connection with immediate disconnection
15. THE Authentication_Verifier SHALL implement a 24-hour cooldown period between authentication attempts for the same credential
16. THE Authentication_Verifier SHALL limit authentication attempts to 1 per credential per day to prevent lockouts
17. WHEN authentication succeeds, THE Authentication_Verifier SHALL mark the credential as "Valid"
18. WHEN authentication fails, THE Authentication_Verifier SHALL mark the credential as "Invalid"
19. THE Authentication_Verifier SHALL log all authentication attempts with timestamps for audit purposes

### Requirement 9: Extract Server Metadata and Banner Information

**User Story:** As a security researcher, I want to extract server metadata and banner information, so that I can assess the security posture of discovered servers.

#### Acceptance Criteria

1. WHEN network connectivity is established, THE Network_Verifier SHALL capture service banner information
2. WHEN SSH connections are established, THE Network_Verifier SHALL extract SSH server version and supported algorithms
3. WHEN FTP connections are established, THE Network_Verifier SHALL extract FTP server software and version
4. WHEN RDP connections are established, THE Network_Verifier SHALL extract Windows version and RDP service information
5. WHEN VNC connections are established, THE Network_Verifier SHALL extract VNC server software and version
6. WHEN SMTP connections are established, THE Network_Verifier SHALL extract SMTP server software and version
7. WHEN IMAP connections are established, THE Network_Verifier SHALL extract IMAP server software and version
8. WHEN cPanel connections are established, THE Network_Verifier SHALL extract cPanel version and hosting provider information
9. WHEN web server connections are established, THE Network_Verifier SHALL extract server headers and technology stack information
10. WHEN database connections are established, THE Network_Verifier SHALL extract database server version and type
11. WHEN HTTP services are detected, THE Network_Verifier SHALL extract server headers and technology stack information
12. THE Network_Verifier SHALL perform OS fingerprinting based on TCP stack characteristics
13. THE Network_Verifier SHALL extract SSL/TLS certificate information when applicable
14. THE Network_Verifier SHALL store all extracted metadata in the database for analysis
15. WHEN banner extraction fails, THE Network_Verifier SHALL store "Banner extraction failed" in the metadata

### Requirement 8: Integrate OSINT Services for Enhanced Intelligence

**User Story:** As a security researcher, I want to integrate OSINT services for discovered IP addresses, so that I can gather additional intelligence about the servers.

#### Acceptance Criteria

1. WHEN IP addresses are extracted from credentials, THE OSINT_Service SHALL query Shodan for historical scan data
2. WHEN IP addresses are extracted from credentials, THE OSINT_Service SHALL query Censys for certificate and service information
3. WHEN IP addresses are extracted from credentials, THE OSINT_Service SHALL query GreyNoise to identify honeypots and scanning activity
4. WHEN OSINT queries return results, THE OSINT_Service SHALL extract open ports, services, and vulnerabilities
5. WHEN GreyNoise identifies an IP as a honeypot, THE OSINT_Service SHALL flag the credential as "Potential Honeypot"
6. THE OSINT_Service SHALL implement rate limiting to respect API quotas (1 request per 5 seconds for free tiers)
7. THE OSINT_Service SHALL cache OSINT results for 24 hours to avoid duplicate queries
8. WHEN OSINT services are unavailable, THE OSINT_Service SHALL continue processing without blocking verification
9. THE OSINT_Service SHALL store OSINT metadata in JSON format in the database

### Requirement 9: Perform Geolocation and ISP Analysis

**User Story:** As a security researcher, I want to identify the geographic location and ISP of discovered servers, so that I can assess the risk and origin of exposed credentials.

#### Acceptance Criteria

1. WHEN IP addresses are extracted from credentials, THE Geolocation_Service SHALL determine the country of origin
2. WHEN IP addresses are extracted from credentials, THE Geolocation_Service SHALL determine the city and region
3. WHEN IP addresses are extracted from credentials, THE Geolocation_Service SHALL identify the Internet Service Provider (ISP)
4. WHEN IP addresses are extracted from credentials, THE Geolocation_Service SHALL identify the Autonomous System Number (ASN)
5. THE Geolocation_Service SHALL detect cloud provider IP ranges for AWS, Azure, GCP, DigitalOcean, Linode, Vultr, Hetzner, and Oracle Cloud
6. WHEN cloud provider IP ranges are detected, THE Geolocation_Service SHALL flag the credential as "Cloud Infrastructure"
7. THE Geolocation_Service SHALL use MaxMind GeoLite2 database for offline geolocation to avoid API dependencies
8. THE Geolocation_Service SHALL store geolocation data in the database for reporting and analysis
9. WHEN geolocation fails, THE Geolocation_Service SHALL store "Geolocation unavailable" in the metadata

### Requirement 10: Implement Advanced Search Techniques

**User Story:** As a security researcher, I want to use advanced search techniques to discover credentials in various file types, so that I can maximize detection coverage.

#### Acceptance Criteria

1. WHEN scanning repositories, THE Server_Credential_Provider SHALL search terminal history files (.bash_history, .zsh_history, .fish_history)
2. WHEN scanning repositories, THE Server_Credential_Provider SHALL search configuration files (config.json, .env, docker-compose.yml)
3. WHEN scanning repositories, THE Server_Credential_Provider SHALL search private key files (id_rsa, id_dsa, id_ecdsa, id_ed25519)
4. WHEN scanning repositories, THE Source_Map_Parser SHALL parse JavaScript source map files (.js.map) to find original source code
5. WHEN scanning repositories, THE SQL_Dump_Scanner SHALL scan database dump files (.sql, .bak, .dump) for embedded credentials
6. THE Server_Credential_Provider SHALL search backup and archive files for credential patterns
7. THE Server_Credential_Provider SHALL search log files for authentication attempts and credential leaks
8. THE Context_Extractor SHALL extract 10 lines of surrounding context for each discovered credential
9. THE Entropy_Analyzer SHALL calculate Shannon entropy for potential passwords to identify high-randomness strings

### Requirement 11: Optimize for Render Free Tier Deployment

**User Story:** As a system operator, I want the server credential detection to work within Render free tier constraints, so that the system remains cost-effective and functional.

#### Acceptance Criteria

1. WHEN running on Render free tier, THE Render_Optimizer SHALL limit concurrent file scans to 2 operations maximum
2. WHEN running on Render free tier, THE Render_Optimizer SHALL limit concurrent verifications to 1 operation maximum
3. WHEN running on Render free tier, THE Render_Optimizer SHALL reduce verification batch size to 10 credentials maximum
4. WHEN running on Render free tier, THE Render_Optimizer SHALL limit maximum file size to 10MB for scanning
5. WHEN running on Render free tier, THE Render_Optimizer SHALL limit maximum files per scan to 100 files
6. THE Render_Optimizer SHALL evaluate Direct I/O performance and use it if beneficial, otherwise fall back to streaming file processing
7. WHEN Direct I/O is not available or performs poorly, THE Render_Optimizer SHALL use streaming file processing for files larger than 1MB
8. THE Render_Optimizer SHALL implement aggressive garbage collection after every 10 operations
9. THE Render_Optimizer SHALL use a 32KB buffer size instead of 64KB for file operations on Render free tier
10. WHEN memory usage exceeds 400MB, THE Render_Optimizer SHALL pause operations and trigger garbage collection
11. THE Render_Optimizer SHALL automatically detect the optimal I/O strategy based on platform capabilities and file characteristics

### Requirement 12: Implement Memory-Efficient Pattern Management

**User Story:** As a system operator, I want efficient regex pattern management, so that memory usage remains within Render free tier limits.

#### Acceptance Criteria

1. WHEN running on Render free tier, THE Pattern_Compiler SHALL load only essential credential patterns (15 patterns maximum)
2. WHEN running on standard deployment, THE Pattern_Compiler SHALL load all credential patterns (50+ patterns)
3. THE Pattern_Compiler SHALL use lazy loading to compile patterns only when needed
4. THE Pattern_Compiler SHALL cache compiled patterns in memory for reuse
5. WHEN memory pressure is detected, THE Pattern_Compiler SHALL dispose of unused pattern objects
6. THE Pattern_Compiler SHALL prioritize high-value patterns (SSH, database, cloud credentials) over low-value patterns
7. THE Pattern_Compiler SHALL use RegexOptions.Compiled only for frequently used patterns
8. THE Pattern_Compiler SHALL implement pattern rotation to manage memory usage
9. WHEN pattern compilation fails due to memory constraints, THE Pattern_Compiler SHALL fall back to non-compiled patterns

### Requirement 13: Store Server Credential Metadata in Database

**User Story:** As a developer, I want server credential metadata stored in the database, so that it persists across application restarts and can be queried.

#### Acceptance Criteria

1. THE Database_Context SHALL add a "ServerCredentials" table with columns for credential type, host, username, and metadata
2. THE Database_Context SHALL add a "CredentialType" column (SSH, FTP, RDP, VNC, WinRM, SMTP, IMAP, POP3, cPanel, WHM, Plesk, Database, Cloud, Container, WebServer)
3. THE Database_Context SHALL add a "Host" column for storing IP addresses or hostnames
4. THE Database_Context SHALL add a "Port" column for storing service ports
5. THE Database_Context SHALL add a "Username" column for storing extracted usernames
6. THE Database_Context SHALL add a "NetworkStatus" column (Accessible, Unreachable, Unknown)
7. THE Database_Context SHALL add a "AuthenticationStatus" column (Valid, Invalid, Untested, Rate Limited)
8. THE Database_Context SHALL add a "ServerMetadata" column for storing JSON metadata (banners, versions, certificates)
9. THE Database_Context SHALL add a "GeolocationData" column for storing JSON geolocation information
10. THE Database_Context SHALL add a "OSINTData" column for storing JSON OSINT intelligence
11. THE Database_Context SHALL add a "RiskLevel" column (Critical, High, Medium, Low) based on credential type and permissions
12. THE Database_Context SHALL add a "IsHoneypot" column (BOOLEAN) for flagging potential security traps
13. THE master_init.sql script SHALL include CREATE TABLE statements for ServerCredentials table

### Requirement 14: Export Server Credential Data

**User Story:** As a security researcher, I want to export discovered server credentials with all metadata, so that I can analyze and report findings.

#### Acceptance Criteria

1. WHEN exporting to CSV format, THE Export_Service SHALL include all ServerCredentials table columns
2. WHEN exporting to JSON format, THE Export_Service SHALL include nested objects for metadata, geolocation, and OSINT data
3. WHEN exporting server credentials, THE Export_Service SHALL format JSON columns as readable strings for CSV export
4. THE Export_Service SHALL include network connectivity status and authentication results in exports
5. THE Export_Service SHALL include risk level and honeypot flags in exports
6. THE Export_Service SHALL provide filtering options by credential type, risk level, and authentication status
7. WHEN server metadata is null, THE Export_Service SHALL export empty strings for CSV and null values for JSON
8. THE Export_Service SHALL include export timestamps and source repository information
9. THE Export_Service SHALL support bulk export of all server credentials or filtered subsets

### Requirement 15: Integrate with Existing CLI Interface

**User Story:** As a security researcher, I want to see server credential results in the CLI interface, so that I can quickly assess discovered credentials.

#### Acceptance Criteria

1. WHEN displaying server credentials in the CLI, THE Program SHALL show the credential type, host, and username
2. WHEN displaying server credentials in the CLI, THE Program SHALL show the network connectivity status with color coding
3. WHEN displaying server credentials in the CLI, THE Program SHALL show the authentication status with color coding
4. WHEN displaying server credentials in the CLI, THE Program SHALL show the risk level with appropriate color coding (red for Critical, yellow for High)
5. WHEN honeypot flags are set, THE Program SHALL display a warning message in yellow
6. WHEN geolocation data is available, THE Program SHALL display country and ISP information
7. THE Program SHALL format server credential data in a readable table format using Spectre.Console
8. THE Program SHALL provide filtering and sorting options for server credential display
9. WHEN server metadata is not available, THE Program SHALL display "N/A" for metadata fields

### Requirement 16: Implement Safe Verification Queue System

**User Story:** As a system operator, I want a queue system for credential verification, so that verification is performed safely and efficiently without overwhelming target servers.

#### Acceptance Criteria

1. THE Verification_Queue SHALL implement a priority queue system with credential risk level determining priority
2. THE Verification_Queue SHALL enforce a minimum 10-second delay between verification attempts to the same host
3. THE Verification_Queue SHALL implement exponential backoff for failed verification attempts (10s, 30s, 60s, 300s)
4. THE Verification_Queue SHALL track verification attempt history to prevent excessive retries
5. WHEN verification queue exceeds 1000 items, THE Verification_Queue SHALL pause new additions and process existing items
6. THE Verification_Queue SHALL implement graceful shutdown to complete in-progress verifications
7. THE Verification_Queue SHALL persist queue state to database to survive application restarts
8. WHEN network errors occur during verification, THE Verification_Queue SHALL automatically retry with backoff
9. THE Verification_Queue SHALL provide progress reporting and estimated completion times

### Requirement 17: Add Server Credential Search Queries

**User Story:** As a security researcher, I want the scraper to automatically search for server credentials, so that I don't need to manually configure search queries.

#### Acceptance Criteria

1. THE Database_Context SeedDefaultDataAsync method SHALL add "ssh " to the default search queries
2. THE Database_Context SeedDefaultDataAsync method SHALL add "ftp://" to the default search queries
3. THE Database_Context SeedDefaultDataAsync method SHALL add "mysql://" to the default search queries
4. THE Database_Context SeedDefaultDataAsync method SHALL add "postgresql://" to the default search queries
5. THE Database_Context SeedDefaultDataAsync method SHALL add "mongodb://" to the default search queries
6. THE Database_Context SeedDefaultDataAsync method SHALL add "redis://" to the default search queries
7. THE Database_Context SeedDefaultDataAsync method SHALL add "-----BEGIN RSA PRIVATE KEY-----" to the default search queries
8. THE Database_Context SeedDefaultDataAsync method SHALL add "KUBERNETES_SERVICE_HOST" to the default search queries
9. THE Database_Context SeedDefaultDataAsync method SHALL add "DOCKER_HOST" to the default search queries
9. THE Database_Context SeedDefaultDataAsync method SHALL add "rdp://" to the default search queries
10. THE Database_Context SeedDefaultDataAsync method SHALL add "vnc://" to the default search queries
11. THE Database_Context SeedDefaultDataAsync method SHALL add "mstsc" to the default search queries
12. THE Database_Context SeedDefaultDataAsync method SHALL add "TeamViewer" to the default search queries
13. THE Database_Context SeedDefaultDataAsync method SHALL add "filename:.rdp" to the default search queries
14. THE Database_Context SeedDefaultDataAsync method SHALL add "WinRM" to the default search queries
15. THE Database_Context SeedDefaultDataAsync method SHALL add "smtp://" to the default search queries
16. THE Database_Context SeedDefaultDataAsync method SHALL add "SMTP_HOST" to the default search queries
17. THE Database_Context SeedDefaultDataAsync method SHALL add "imap://" to the default search queries
18. THE Database_Context SeedDefaultDataAsync method SHALL add "pop3://" to the default search queries
19. THE Database_Context SeedDefaultDataAsync method SHALL add "cPanel" to the default search queries
20. THE Database_Context SeedDefaultDataAsync method SHALL add "WHM_USER" to the default search queries
21. THE Database_Context SeedDefaultDataAsync method SHALL add "PLESK_" to the default search queries
22. THE Database_Context SeedDefaultDataAsync method SHALL add filename:.bash_history to the default search queries
23. THE Database_Context SeedDefaultDataAsync method SHALL add filename:id_rsa to the default search queries
24. THE Database_Context SeedDefaultDataAsync method SHALL add extension:env to the default search queries
13. THE master_init.sql script SHALL include INSERT statements for server credential search queries
14. THE search queries SHALL be marked as enabled by default

### Requirement 18: Handle Verification Errors and Edge Cases

**User Story:** As a system operator, I want the system to handle verification errors gracefully, so that the system remains stable and continues processing.

#### Acceptance Criteria

1. WHEN network timeouts occur during verification, THE Authentication_Verifier SHALL mark the credential as "Network Timeout" and continue processing
2. WHEN authentication attempts result in connection refused, THE Authentication_Verifier SHALL mark the credential as "Service Unavailable"
3. WHEN authentication attempts trigger rate limiting, THE Authentication_Verifier SHALL mark the credential as "Rate Limited" and implement exponential backoff
4. WHEN SSL/TLS certificate errors occur, THE Authentication_Verifier SHALL mark the credential as "Certificate Error" but continue with insecure connection if possible
5. WHEN authentication attempts result in account lockout warnings, THE Authentication_Verifier SHALL immediately stop attempts for that host and mark as "Lockout Risk"
6. THE Authentication_Verifier SHALL implement circuit breaker pattern to stop verification for hosts that consistently fail
7. WHEN verification services are unavailable (OSINT, geolocation), THE system SHALL continue processing without blocking
8. THE system SHALL log all verification errors with detailed context for debugging
9. WHEN maximum error threshold is reached (50% failure rate), THE system SHALL pause verification and alert operators
