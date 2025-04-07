
// PN532_NFC_App.h : main header file for the PROJECT_NAME application
//

#pragma once

#ifndef __AFXWIN_H__
	#error "include 'pch.h' before including this file for PCH"
#endif

#include "resource.h"		// main symbols


// CPN532NFCAppApp:
// See PN532_NFC_App.cpp for the implementation of this class
//

class CPN532NFCAppApp : public CWinApp
{
public:
	CPN532NFCAppApp();

// Overrides
public:
	virtual BOOL InitInstance();

// Implementation

	DECLARE_MESSAGE_MAP()
};

extern CPN532NFCAppApp theApp;
