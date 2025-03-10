// src/app/interceptors/session-id.interceptor.ts

import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { LoggingService } from '../services/logging.service';

export const sessionIdInterceptor: HttpInterceptorFn = (req, next) => {
  const loggingService = inject(LoggingService);
  
  // Skip adding session ID for logging API calls to prevent circular dependencies
  if (req.url.includes('/logs')) {
    return next(req);
  }
  
  // Clone the request and add the session ID header
  const sessionId = loggingService.getSessionId();
  const modifiedReq = req.clone({
    setHeaders: {
      'X-Session-ID': sessionId
    }
  });
  
  // Pass the modified request on to the next handler
  return next(modifiedReq);
};
