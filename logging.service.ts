// src/app/services/logging.service.ts

import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { v4 as uuidv4 } from 'uuid'; // You'll need to install uuid package

@Injectable({
  providedIn: 'root'
})
export class LoggingService {
  private readonly SESSION_ID_KEY = 'app_session_id';
  private readonly LOGGING_API_URL = 'https://your-logging-api.com/logs';
  private http = inject(HttpClient);
  
  constructor() {
    this.ensureSessionId();
  }

  /**
   * Ensures a session ID exists in sessionStorage or creates a new one
   */
  private ensureSessionId(): string {
    let sessionId = sessionStorage.getItem(this.SESSION_ID_KEY);
    
    if (!sessionId) {
      sessionId = uuidv4();
      sessionStorage.setItem(this.SESSION_ID_KEY, sessionId);
    }
    
    return sessionId;
  }

  /**
   * Get the current session ID
   */
  getSessionId(): string {
    return this.ensureSessionId();
  }

  /**
   * Log an event to the remote logging API
   */
  log(level: 'info' | 'warn' | 'error', message: string, additionalData?: any): void {
    const sessionId = this.getSessionId();
    
    const logData = {
      timestamp: new Date().toISOString(),
      sessionId,
      level,
      message,
      source: 'frontend',
      additionalData,
      userAgent: navigator.userAgent,
      url: window.location.href
    };
    
    this.http.post(this.LOGGING_API_URL, logData).subscribe({
      next: () => {},
      error: (err) => {
        // Fallback logging if remote logging fails
        console.error('Failed to send log to remote API', err);
      }
    });
  }

  /**
   * Get HTTP headers containing the session ID for API calls
   */
  getSessionHeaders(): HttpHeaders {
    return new HttpHeaders({
      'X-Session-ID': this.getSessionId()
    });
  }
}
