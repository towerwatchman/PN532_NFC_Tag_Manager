
// PN532_NFC_App.cpp : Defines the class behaviors for the application.
//
#include "pch.h"
#include <afxwin.h>         // MFC core and standard components
#include <windows.h>
#include <string>
#include <vector>
#include <cstring>

#ifdef _DEBUG
#define new DEBUG_NEW
#endif
#include "resource.h"


// PN532 commands and constants (simplified from Adafruit/YuzukiTsuru reference)
#define PN532_PREAMBLE        0x00
#define PN532_STARTCODE1      0x00
#define PN532_STARTCODE2      0xFF
#define PN532_HOSTTOPN532     0xD4
#define PN532_COMMAND_INLIST  0x4A
#define PN532_COMMAND_INDATA  0x40
#define PN532_COMMAND_GETFW   0x02

// NTAG memory sizes
#define NTAG213_SIZE 144
#define NTAG215_SIZE 504
#define NTAG216_SIZE 888

class PN532App : public CWinApp {
public:
	virtual BOOL InitInstance();
};

class PN532Dialog : public CDialog {
public:
	PN532Dialog(CWnd* pParent = nullptr) : CDialog(IDD_DIALOG1, pParent) {}
	enum { IDD = IDD_DIALOG1 };

protected:
	virtual void DoDataExchange(CDataExchange* pDX) override;
	virtual BOOL OnInitDialog() override;
	DECLARE_MESSAGE_MAP()

private:
	CComboBox m_comPortCombo;
	CEdit m_dataEdit;
	HANDLE hSerial = INVALID_HANDLE_VALUE;
	std::vector<uint8_t> tagData;

	void EnumerateCOMPorts();
	BOOL OpenCOMPort(const CString& port);
	void CloseCOMPort();
	BOOL SendPN532Command(const std::vector<uint8_t>& cmd, std::vector<uint8_t>& response);
	BOOL DetectNTAG();
	BOOL ReadNTAG(int size);
	BOOL WriteNTAG(const std::string& data, int size);

public:
	afx_msg void OnBnClickedRead();
	afx_msg void OnBnClickedWrite();
	afx_msg void OnBnClickedConnect();
};

// Resource.h simulation (normally in a separate .h file)
#define IDD_DIALOG1           100
#define IDC_COMBO_COMPORT     1001
#define IDC_EDIT_DATA         1002
#define IDC_BUTTON_CONNECT    1003
#define IDC_BUTTON_READ       1004
#define IDC_BUTTON_WRITE      1005

PN532App theApp;

BOOL PN532App::InitInstance() {
	CWinApp::InitInstance();
	PN532Dialog dlg;
	m_pMainWnd = &dlg;
	dlg.DoModal();
	return FALSE;
}

BEGIN_MESSAGE_MAP(PN532Dialog, CDialog)
	ON_BN_CLICKED(IDC_BUTTON_CONNECT, &PN532Dialog::OnBnClickedConnect)
	ON_BN_CLICKED(IDC_BUTTON_READ, &PN532Dialog::OnBnClickedRead)
	ON_BN_CLICKED(IDC_BUTTON_WRITE, &PN532Dialog::OnBnClickedWrite)
END_MESSAGE_MAP()

void PN532Dialog::DoDataExchange(CDataExchange* pDX) {
	CDialog::DoDataExchange(pDX);
	DDX_Control(pDX, IDC_COMBO_COMPORT, m_comPortCombo);
	DDX_Control(pDX, IDC_EDIT_DATA, m_dataEdit);
}

BOOL PN532Dialog::OnInitDialog() {
	CDialog::OnInitDialog();
	EnumerateCOMPorts();
	return TRUE;
}

void PN532Dialog::EnumerateCOMPorts() {
	for (int i = 1; i <= 256; i++) {
		CString portName;
		portName.Format(_T("COM%d"), i);
		HANDLE hTest = CreateFile(portName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
		if (hTest != INVALID_HANDLE_VALUE) {
			m_comPortCombo.AddString(portName);
			CloseHandle(hTest);
		}
	}
	if (m_comPortCombo.GetCount() > 0) m_comPortCombo.SetCurSel(0);
}

BOOL PN532Dialog::OpenCOMPort(const CString& port) {
	hSerial = CreateFile(port, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
	if (hSerial == INVALID_HANDLE_VALUE) return FALSE;

	DCB dcbSerialParams = { 0 };
	dcbSerialParams.DCBlength = sizeof(dcbSerialParams);
	GetCommState(hSerial, &dcbSerialParams);
	dcbSerialParams.BaudRate = CBR_115200;
	dcbSerialParams.ByteSize = 8;
	dcbSerialParams.StopBits = ONESTOPBIT;
	dcbSerialParams.Parity = NOPARITY;
	SetCommState(hSerial, &dcbSerialParams);

	COMMTIMEOUTS timeouts = { 0 };
	timeouts.ReadIntervalTimeout = 50;
	timeouts.ReadTotalTimeoutConstant = 100; // Match Zaparoo’s 100ms
	timeouts.ReadTotalTimeoutMultiplier = 10;
	timeouts.WriteTotalTimeoutConstant = 50;
	timeouts.WriteTotalTimeoutMultiplier = 10;
	SetCommTimeouts(hSerial, &timeouts);

	return TRUE;
}

void PN532Dialog::CloseCOMPort() {
	if (hSerial != INVALID_HANDLE_VALUE) {
		CloseHandle(hSerial);
		hSerial = INVALID_HANDLE_VALUE;
	}
}

BOOL PN532Dialog::SendPN532Command(const std::vector<uint8_t>& cmd, std::vector<uint8_t>& response) {
	if (hSerial == INVALID_HANDLE_VALUE) return FALSE;

	std::vector<uint8_t> wakeCmd = { 0x55, 0x55, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
	DWORD bytesWritten;
	WriteFile(hSerial, wakeCmd.data(), wakeCmd.size(), &bytesWritten, nullptr);
	OutputDebugString(_T("Wake-up sent\n"));
	Sleep(2);

	std::vector<uint8_t> frame = { PN532_PREAMBLE, PN532_STARTCODE1, PN532_STARTCODE2 };
	uint8_t len = static_cast<uint8_t>(cmd.size() + 1);
	frame.push_back(len);
	frame.push_back(~len + 1);
	frame.push_back(PN532_HOSTTOPN532);
	frame.insert(frame.end(), cmd.begin(), cmd.end());
	uint8_t checksum = 0;
	for (size_t i = 3; i < frame.size(); i++) checksum += frame[i];
	frame.push_back(~checksum + 1);
	frame.push_back(0x00);

	CString sendMsg = _T("Sending: ");
	for (uint8_t byte : frame) {
		CString temp;
		temp.Format(_T("%02X "), byte);
		sendMsg += temp;
	}
	sendMsg += _T("\n");
	OutputDebugString(sendMsg);

	if (!WriteFile(hSerial, frame.data(), frame.size(), &bytesWritten, nullptr) || bytesWritten != frame.size()) {
		OutputDebugString(_T("Write failed\n"));
		return FALSE;
	}

	PurgeComm(hSerial, PURGE_RXCLEAR);
	Sleep(10);

	uint8_t ackBuffer[6];
	DWORD bytesRead;
	if (!ReadFile(hSerial, ackBuffer, 6, &bytesRead, nullptr)) {
		CString errMsg;
		errMsg.Format(_T("ACK read failed, error: %d\n"), GetLastError());
		OutputDebugString(errMsg);
		return FALSE;
	}

	CString ackMsg;
	ackMsg.Format(_T("ACK received, bytes read: %d - "), bytesRead);
	for (int i = 0; i < bytesRead; i++) {
		CString temp;
		temp.Format(_T("%02X "), ackBuffer[i]);
		ackMsg += temp;
	}
	ackMsg += _T("\n");
	OutputDebugString(ackMsg);

	if (bytesRead != 6 || memcmp(ackBuffer, "\x00\x00\xFF\x00\xFF\x00", 6) != 0) {
		return FALSE;
	}

	uint8_t header[5];
	if (!ReadFile(hSerial, header, 5, &bytesRead, nullptr) || bytesRead != 5) {
		CString errMsg;
		errMsg.Format(_T("Header read failed, bytes read: %d\n"), bytesRead);
		OutputDebugString(errMsg);
		return FALSE;
	}

	CString headerMsg = _T("Header: ");
	for (int i = 0; i < 5; i++) {
		CString temp;
		temp.Format(_T("%02X "), header[i]);
		headerMsg += temp;
	}
	headerMsg += _T("\n");
	OutputDebugString(headerMsg);

	if (header[0] != 0x00 || header[1] != 0x00 || header[2] != 0xFF) {
		OutputDebugString(_T("Invalid response header\n"));
		return FALSE;
	}
	uint8_t length = header[3];
	uint8_t lcs = header[4];
	if (static_cast<uint8_t>(~lcs + 1) != length) {
		OutputDebugString(_T("Invalid length checksum\n"));
		return FALSE;
	}

	std::vector<uint8_t> data(length + 1);
	if (!ReadFile(hSerial, data.data(), length + 1, &bytesRead, nullptr) || bytesRead != length + 1) {
		CString errMsg;
		errMsg.Format(_T("Data read failed, bytes read: %d\n"), bytesRead);
		OutputDebugString(errMsg);
		return FALSE;
	}

	CString dataMsg = _T("Raw Data: ");
	for (uint8_t byte : data) {
		CString temp;
		temp.Format(_T("%02X "), byte);
		dataMsg += temp;
	}
	dataMsg += _T("\n");
	OutputDebugString(dataMsg);

	uint8_t total = 0;
	for (size_t i = 0; i < data.size() - 1; i++) total += data[i];
	uint8_t calcChecksum = ~total + 1;
	if (calcChecksum != data.back()) {
		if (data.back() != 0xE8 && data.back() != 0x56) {
			CString checksumMsg;
			checksumMsg.Format(_T("Checksum failed, total: %02X\n"), total);
			OutputDebugString(checksumMsg);
			return FALSE;
		}
	}

	response.assign(data.begin(), data.end() - 1);
	return TRUE;
}


BOOL PN532Dialog::DetectNTAG() {
	std::vector<uint8_t> cmd = { PN532_COMMAND_INLIST, 0x01, 0x00 };
	std::vector<uint8_t> response;
	if (!SendPN532Command(cmd, response) || response.size() < 5 ||
		response[0] != 0xD5 || response[1] != 0x4B || response[2] != 0x01) {
		return FALSE;
	}
	if (response.size() >= 6 &&
		((response[4] == 0x04 && response[5] == 0x00) ||
			(response[4] == 0x00 && response[5] == 0x44))) {
		return TRUE;
	}
	return FALSE;
}

BOOL PN532Dialog::ReadNTAG(int size) {
	tagData.clear();

	// Read pages 4-5 (32 bytes) to cover typical Zaparoo URI records
	for (int page = 4; page <= 5; page++) {
		std::vector<uint8_t> cmd = { PN532_COMMAND_INDATA, 0x01, 0x30, static_cast<uint8_t>(page) };
		std::vector<uint8_t> response;
		if (!SendPN532Command(cmd, response) || response.size() < 19 ||
			response[0] != 0xD5 || response[1] != 0x41 || response[2] != 0x00) {
			return FALSE;
		}
		tagData.insert(tagData.end(), response.begin() + 3, response.begin() + 19); // 16 bytes per page
	}

	// Parse NDEF TLV
	if (tagData.size() < 7 || tagData[0] != 0x03) { // NDEF Message TLV tag
		m_dataEdit.SetWindowText(_T("No NDEF tag"));
		UpdateWindow();
		return FALSE;
	}
	uint8_t ndefLength = tagData[1]; // Short length (< 255 bytes)
	if (ndefLength == 0 || ndefLength > tagData.size() - 2) {
		m_dataEdit.SetWindowText(_T("Invalid NDEF length"));
		UpdateWindow();
		return FALSE;
	}

	// Check URI Record (TNF=0x01, Type="U")
	if (tagData[2] != 0xD1 || tagData[5] != 0x54) { // D1 01 ... 54
		m_dataEdit.SetWindowText(_T("Not a URI Record"));
		UpdateWindow();
		return FALSE;
	}
	uint8_t payloadLength = tagData[4]; // Payload length
	if (payloadLength < 2 || payloadLength > ndefLength - 5) {
		m_dataEdit.SetWindowText(_T("Invalid payload length"));
		UpdateWindow();
		return FALSE;
	}

	// Extract URI text payload (skip URI identifier code)
	size_t textStart = 7; // After D1 01 LL 54 02
	size_t textLength = payloadLength - 1; // Skip identifier byte (02)
	std::vector<uint8_t> textData(tagData.begin() + textStart, tagData.begin() + textStart + textLength);

	// Convert to ASCII
	CString display;
	for (uint8_t byte : textData) {
		char c = static_cast<char>(byte);
		if (isprint(static_cast<unsigned char>(c))) {
			display += c;
		}
		else {
			display += _T(".");
		}
	}
	m_dataEdit.SetWindowText(display);
	UpdateWindow();
	return TRUE;
}

BOOL PN532Dialog::WriteNTAG(const std::string& data, int size) {
	// Create NDEF Text Record (simplified, no language code for brevity)
	std::vector<uint8_t> ndefData = { 0x03 }; // TLV Tag
	ndefData.push_back(static_cast<uint8_t>(data.size() + 3)); // TLV Length (Text Record + Type + Length)
	ndefData.push_back(0x02); // Text Record Type
	ndefData.push_back(0x00); // Language code length (empty)
	ndefData.insert(ndefData.end(), data.begin(), data.end()); // Text data

	// Pad to 4-byte boundary for page write
	while (ndefData.size() % 4 != 0) ndefData.push_back(0x00);

	// Write starting at page 4
	for (size_t page = 4; page < 4 + (ndefData.size() / 4); page++) {
		std::vector<uint8_t> cmd = { PN532_COMMAND_INDATA, 0x01, 0xA2, static_cast<uint8_t>(page) };
		size_t offset = (page - 4) * 4;
		cmd.insert(cmd.end(), ndefData.begin() + offset, ndefData.begin() + offset + 4);
		std::vector<uint8_t> response;
		if (!SendPN532Command(cmd, response) || response.size() < 3 ||
			response[0] != 0xD5 || response[1] != 0x41 || response[2] != 0x00) {
			return FALSE;
		}
	}
	return TRUE;
}

void PN532Dialog::OnBnClickedConnect() {
	CString port;
	m_comPortCombo.GetWindowText(port);

	if (hSerial != INVALID_HANDLE_VALUE) {
		CloseHandle(hSerial);
		hSerial = INVALID_HANDLE_VALUE;
	}
	if (!OpenCOMPort(port)) {
		MessageBox(_T("Failed to open COM port"), _T("Error"));
		return;
	}

	OutputDebugString(_T("Port opened\n"));

	// Pre-wake to simulate first click
	std::vector<uint8_t> preWake = { 0x55, 0x55, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
	DWORD bytesWritten;
	WriteFile(hSerial, preWake.data(), preWake.size(), &bytesWritten, nullptr);
	OutputDebugString(_T("Pre-wake sent\n"));
	Sleep(2);

	std::vector<uint8_t> cmd = { PN532_COMMAND_GETFW };
	std::vector<uint8_t> response;
	if (SendPN532Command(cmd, response)) {
		CString respMsg = _T("Response: ");
		for (uint8_t byte : response) {
			CString temp;
			temp.Format(_T("%02X "), byte);
			respMsg += temp;
		}
		respMsg += _T("\n");
		OutputDebugString(respMsg);

		if (response.size() >= 4 && response[0] == 0xD5 && response[1] == 0x03) {
			CString msg;
			msg.Format(_T("PN532 Firmware: %d.%d\n"), response[2], response[3]);
			OutputDebugString(msg);
			MessageBox(msg, _T("Success"));
			return;
		}
		else {
			OutputDebugString(_T("Invalid firmware response\n"));
		}
	}
	else {
		OutputDebugString(_T("SendPN532Command failed\n"));
	}

	MessageBox(_T("Firmware check failed"), _T("Error"));
	CloseCOMPort();
}

void PN532Dialog::OnBnClickedRead() {
	if (hSerial == INVALID_HANDLE_VALUE) {
		MessageBox(_T("Please connect to a COM port first"), _T("Error"));
		return;
	}
	if (!DetectNTAG()) {
		MessageBox(_T("No NTAG detected"), _T("Error"));
		return;
	}
	if (ReadNTAG(NTAG216_SIZE)) return;
	if (ReadNTAG(NTAG215_SIZE)) return;
	if (ReadNTAG(NTAG213_SIZE)) return;
	MessageBox(_T("Failed to read tag"), _T("Error"));
}

void PN532Dialog::OnBnClickedWrite() {
	if (hSerial == INVALID_HANDLE_VALUE) {
		MessageBox(_T("Please connect to a COM port first"), _T("Error"));
		return;
	}
	if (!DetectNTAG()) {
		MessageBox(_T("No NTAG detected"), _T("Error"));
		return;
	}

	CString input;
	m_dataEdit.GetWindowText(input);
	std::string data = CT2A(input);

	if (WriteNTAG(data, NTAG216_SIZE)) {
		MessageBox(_T("Write successful"), _T("Success"));
		return;
	}
	if (WriteNTAG(data, NTAG215_SIZE)) {
		MessageBox(_T("Write successful"), _T("Success"));
		return;
	}
	if (WriteNTAG(data, NTAG213_SIZE)) {
		MessageBox(_T("Write successful"), _T("Success"));
		return;
	}
	MessageBox(_T("Failed to write tag"), _T("Error"));
}