using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Collections;
using System.IO;

using ILPInstrumentMapper;
using ALCoreObj;
using ALProcEngInstr;


namespace ILPEngineAdapter
{

	// Provides the database context for income simulation and other 
	// consumers of InstrumentProcessor class. Has to be passed to the Init method of
	// InstrumentProcessor

	public class MappingContext : ILPInstrumentMapper.LookUpContext
	{
		private string m_instrTbl;
		private string m_instrDateField ;

		public MappingContext():base()
		{
		}

		public MappingContext(string instrTbl, string baseRateTbl,string instrDateField, DateTime baseRateStartMonth) : base()
		{
			InstrumentTbl = instrTbl;
			BaseRateTbl = baseRateTbl;
			InstrDateField = instrDateField;
            BaseRateStartMonth = baseRateStartMonth;
		}

        // 11/15/07 dkb - applix 43833 - added this overloaded method to allow callers to set the ending base rate month upon initialization.
        public MappingContext(string instrTbl, string baseRateTbl, string instrDateField, DateTime baseRateStartMonth, DateTime baseRateEndMonth): base()
        {
            InstrumentTbl = instrTbl;
            BaseRateTbl = baseRateTbl;
            InstrDateField = instrDateField;
            BaseRateStartMonth = baseRateStartMonth;
            BaseRateEndMonth = baseRateEndMonth;
        }

        public string InstrumentTbl
		{
			get { return m_instrTbl;  }
			set { m_instrTbl = value; }
		}
		public string InstrDateField
		{
			get { return m_instrDateField;  }
			set { m_instrDateField = value; }
		}

	}

	public delegate void InstrumentDataErrorHandler(object sender, InvalidDataArgs e);

	#region InvalidDataArgs definition
	public class InvalidDataArgs : EventArgs
	{
		// Declare private variables to reflect the information
		// about the event
		private readonly int m_instID;// instrument key where the error occurred
		private readonly DateTime m_month; // month when the error occurred
		private readonly InstrumentDataErrorType m_errType;

		//Constructor
		public InvalidDataArgs( int instID, DateTime month, InstrumentDataErrorType type )
		{
			this.m_instID = instID;
			this.m_month = month;
			this.m_errType = type;
		}

		public int InstrumentKey
		{
			get
			{
				return m_instID;
			}
		}
		public DateTime CurrentMonth
		{
			get
			{
				return m_month;
			}
		}
		public InstrumentDataErrorType ErrorType
		{
			get
			{
				return m_errType;
			}
		}

	}

	#endregion


	/// <summary>
	/// Summary description for InstrumentProcessor.
	/// Processes one instrument only for a specified current date
	/// Also this class can be used as a base class for UI purposes specifically as it contains methods
	/// to return a particular type of instrument once the SetInstrumentID is called.
	/// The SeInstrumentID returns the instrument type which is used to call the corresponding
	/// GetXXXInstr() so that UI can use the returned instrument for display purposes.
	/// All the GetXXXInstr() methods have to provided by the derived class to expose the protected
	/// member instrument holders
	/// </summary>
	public class InstrumentProcessor : IDisposable
	{
		protected int m_instr_k;
		protected int m_instrType;
        protected DateTime m_curDate = DateTime.Now;
        protected string m_connString = "";
		private bool m_bDisposed = false;


		protected ALProcEngInstr.ProcEngInstrAmortInput m_instrAmort = null;
		protected ALProcEngInstr.ProcEngInstrBulletInput m_instrBullet = null;
		protected ALProcEngInstr.ProcEngInstrSpreadEvenInput m_instrSpreadEvenly = null;

        protected SpreadEvenlyMapper m_spreadEvenlyMapper = null;
        protected BulletMapper m_bulletMapper = null;
        protected AmortizedMapper m_amortizedMapper = null;

		private ProcEngInstrBullet m_peib = null;
		private ProcEngInstrAmort m_peia = null;
		private ProcEngInstrSpreadEven m_peis = null;
	
		private ProcEngInstrCtrl m_peic = null;
		private LookUpCache	m_lookUpCache = null;

        public event InstrumentDataErrorHandler InstrumentDataError;

		public InstrumentProcessor()
		{
			m_instr_k = -1;
		}

		private void Reset()
		{
			m_instr_k = -1;
			m_instrType = -1;
			m_instrAmort.Reset();
			m_instrBullet.Reset();
			m_instrSpreadEvenly.Reset();
		}

		public virtual void Init(string connString, DateTime curDate, MappingContext mc)
		{
			m_connString = connString;
            m_curDate = new DateTime(curDate.Year, curDate.Month, 1);

			m_instrAmort = new ALProcEngInstr.ProcEngInstrAmortInput();
			m_instrBullet = new ALProcEngInstr.ProcEngInstrBulletInput();
			m_instrSpreadEvenly = new ALProcEngInstr.ProcEngInstrSpreadEvenInput();

			m_peib = new ProcEngInstrBullet();
			m_peia = new ProcEngInstrAmort();
			m_peis = new ProcEngInstrSpreadEven();
		
			m_peic = new ProcEngInstrCtrl();

            m_peic.mLogFlag = false;
            string dumpData = ConfigurationManager.AppSettings["ILPDataDump"];

            if (dumpData == "true")
            {
                string dumpFolder = System.Reflection.Assembly.GetExecutingAssembly().Location;
                dumpFolder = Path.GetDirectoryName(dumpFolder) + "\\ILPDataDump\\";

                m_peic.mLogFlag = true;

                string curMonth = "_" + Convert.ToString(m_curDate.Month) + "_" + Convert.ToString(m_curDate.Year);
                m_peic.mInputDumpFilePath = dumpFolder + "INPDump" + curMonth + ".xls";
                m_peic.mCashFlowDumpFilePath = dumpFolder + "CFDump" + curMonth + ".xls";
                m_peic.mIncAccrDumpFilePath = dumpFolder + "IADump" + curMonth + ".xls";
                m_peic.mEconValDumpFilePath = dumpFolder + "EVDump" + curMonth + ".xls";
                m_peic.mGapDumpFilePath = dumpFolder + "GAPDump" + curMonth + ".xls";

            }


			m_lookUpCache = new LookUpCache();
			m_spreadEvenlyMapper = new SpreadEvenlyMapper();
			m_spreadEvenlyMapper.InstrumentDataError += new InstrDataErrorHandler( OnInstrumentError );
			m_bulletMapper = new BulletMapper();
			m_bulletMapper.InstrumentDataError += new InstrDataErrorHandler( OnInstrumentError );
			m_amortizedMapper = new AmortizedMapper();
			m_amortizedMapper.InstrumentDataError += new InstrDataErrorHandler( OnInstrumentError );

			if( mc != null)
			{
				LookUpContext lc = new LookUpContext();
				lc.BaseRateTbl = mc.BaseRateTbl;
				lc.YieldCurveRateTbl = mc.YieldCurveRateTbl;
				lc.YieldCurveTbl = mc.YieldCurveTbl;
				lc.PrePaymentSpeedTbl = mc.PrePaymentSpeedTbl;
				lc.PrePaymentSpeedDetailTbl = mc.PrePaymentSpeedDetailTbl;
				lc.SpeedPercentageTbl = mc.SpeedPercentageTbl;
                lc.BaseRateStartMonth = mc.BaseRateStartMonth;
                lc.BaseRateEndMonth = mc.BaseRateEndMonth; // 11/15/07 dkb - applix 43833

				m_lookUpCache.Init(m_connString, m_curDate, lc);
			}
			else
			{
				m_lookUpCache.Init(m_connString, m_curDate, null);
			}

            InitMappers(mc);

		}

        protected virtual void InitMappers(MappingContext mc)
        {
            if (mc != null)
            {
                m_spreadEvenlyMapper.Init(m_connString, m_curDate, mc.InstrumentTbl, mc.InstrDateField);
                m_bulletMapper.Init(m_connString, m_curDate, mc.InstrumentTbl, mc.InstrDateField);
                m_amortizedMapper.Init(m_connString, m_curDate, mc.InstrumentTbl, mc.InstrDateField);
            }
            else
            {
                m_spreadEvenlyMapper.Init(m_connString, m_curDate, "BP_INSTRUMENT_HISTORY", "INSTRUMENT_HISTORY_D");
                m_bulletMapper.Init(m_connString, m_curDate, "BP_INSTRUMENT_HISTORY", "INSTRUMENT_HISTORY_D");
                m_amortizedMapper.Init(m_connString, m_curDate, "BP_INSTRUMENT_HISTORY", "INSTRUMENT_HISTORY_D");
            }
        }
		// returns the instrument type mapping it to ILP instrument type
		// as well as retrieving the instrument data

		public virtual int SetInstrument(int instr_k, int iType )
		{

			Reset();
			m_instr_k = instr_k;

			int instType = GetILPInstrumentType(iType);
			switch(instType)
			{
				case ALConsts.InstrTypeBullet:// means At maturity same as Bullet type
					m_instrType = ALCoreObj.ALConsts.InstrTypeBullet;
					m_bulletMapper.GetInstrumentData( m_instrBullet,instr_k, m_lookUpCache);
					break;
				case ALConsts.InstrTypeAmort:// means Amortized
					m_instrType = ALCoreObj.ALConsts.InstrTypeAmort;
					m_amortizedMapper.GetInstrumentData( m_instrAmort,instr_k, m_lookUpCache);
					break;
				case ALConsts.InstrTypeCallPut:// means Call type
					m_instrType = ALCoreObj.ALConsts.InstrTypeCallPut;
					m_bulletMapper.GetInstrumentData( m_instrBullet,instr_k, m_lookUpCache);
					break;
				case ALConsts.InstrTypeSpreadEven:// means Spread Evenly type
					m_instrType = ALCoreObj.ALConsts.InstrTypeSpreadEven;
					m_spreadEvenlyMapper.GetInstrumentData( m_instrSpreadEvenly,instr_k, m_lookUpCache);
					break;
				default:
					m_instrType = ALCoreObj.ALConsts.InstrTypeNone;
					break;
			}

            ApplyBusinessRules();
			return m_instrType;

		}

		public virtual int SetInstrument( SqlDataReader rdr )
		{
			Trace.Assert( rdr != null );
			Trace.Assert( rdr.IsClosed == false );

			Reset();
			m_instr_k = rdr.GetInt32(0);

			int instType = GetILPInstrumentType(rdr.GetInt32(3));

			switch(instType)
			{
				case ALConsts.InstrTypeBullet:// means At maturity same as Bullet type
					m_instrType = ALCoreObj.ALConsts.InstrTypeBullet;
					m_bulletMapper.GetInstrumentData( m_instrBullet, rdr,  m_lookUpCache);
					break;
				case ALConsts.InstrTypeAmort:// means Amortized
					m_instrType = ALCoreObj.ALConsts.InstrTypeAmort;
					m_amortizedMapper.GetInstrumentData( m_instrAmort, rdr,  m_lookUpCache);
					break;
				case ALConsts.InstrTypeCallPut:// means Call type
					m_instrType = ALCoreObj.ALConsts.InstrTypeCallPut;
					m_bulletMapper.GetInstrumentData( m_instrBullet, rdr,  m_lookUpCache);
					break;
				case ALConsts.InstrTypeSpreadEven:// means Spread Evenly type
					m_instrType = ALCoreObj.ALConsts.InstrTypeSpreadEven;
					m_spreadEvenlyMapper.GetInstrumentData( m_instrSpreadEvenly, rdr,  m_lookUpCache);
					break;
				default:
					m_instrType = ALCoreObj.ALConsts.InstrTypeNone;
					break;
			}

            ApplyBusinessRules();

			return m_instrType;

		}


		public virtual void Calculate( ALProcEngInstr.CashFlow cf,  ALProcEngInstr.IncAccr ia,  ALProcEngInstr.EconVal[] ev )
		{
	

			if(cf != null)
				m_peic.mCalcCashFlowFlag = true;
			else
				m_peic.mCalcCashFlowFlag = false;

			if(ia != null)
			{
				m_peic.mCalcIncAccrFlag = true;
				m_peic.mIncAccrStartDate = new ALCoreObj.Date(m_curDate.Year, m_curDate.Month, m_curDate.Day);
				m_peic.mNumIncAccrPd = 120;
			}
			else
				m_peic.mCalcIncAccrFlag = false;

			if(ev != null && ev[0] != null)
			{
				m_peic.mCalcEconValFlag = true;
				m_peic.mNumEconValPt = 1;
				m_peic.mEconValPt[0] = new ALCoreObj.Date(m_curDate.Year, m_curDate.Month, m_curDate.Day);
			}
			else
				m_peic.mCalcEconValFlag = false;

			m_peic.mCalcGapFlag = false;

			if(m_instrType == ALCoreObj.ALConsts.InstrTypeBullet )
			{
				m_peib.Reset();
				m_peib.ProcInstr(m_peic, m_instrBullet, cf, null, ia, null, ev, null, null, null);
				return;
			}
			if(m_instrType == ALCoreObj.ALConsts.InstrTypeCallPut )
			{
				m_peib.Reset();
				m_peib.ProcInstr(m_peic, m_instrBullet, cf, null, ia, null, ev, null, null, null);
				return;
			}
			if(m_instrType == ALCoreObj.ALConsts.InstrTypeAmort)
			{
				m_peia.Reset();
				m_peia.ProcInstr(m_peic, m_instrAmort, cf, null, ia, null, ev, null, null, null);
				return;
			}
			if(m_instrType == ALCoreObj.ALConsts.InstrTypeSpreadEven)
			{
				m_peis.Reset();
				m_peis.ProcInstr(m_peic, m_instrSpreadEvenly, cf, null, ia, null, ev, null, null, null);
				return;
			}
			
		}

		// This will be called whenever the instrument mapper encounters an error in the instrument repricing data
		protected void OnInstrumentError(object sender, DataErrorArgs e) 
		{
            Trace.Assert(e != null);
            if (InstrumentDataError != null)
                InstrumentDataError(this, new InvalidDataArgs(e.InstrumentKey, e.CurrentMonth, e.ErrorType));

		}


		#region SetEVDiscMethod

		public virtual void SetEVDiscMethod(int evDiscMeth)
		{
			if(m_instrType == ALCoreObj.ALConsts.InstrTypeBullet )
			{
				m_instrBullet.mEconValDiscMthd = evDiscMeth;
				return;
			}
            if (m_instrType == ALCoreObj.ALConsts.InstrTypeCallPut)
            {
                m_instrBullet.mEconValDiscMthd = evDiscMeth;
                return;
            }
			if(m_instrType == ALCoreObj.ALConsts.InstrTypeAmort)
			{
				m_instrAmort.mEconValDiscMthd = evDiscMeth;
				return;
			}
			if(m_instrType == ALCoreObj.ALConsts.InstrTypeSpreadEven)
			{
				m_instrSpreadEvenly.mEconValDiscMthd = evDiscMeth;
				return;
			}

		}
		#endregion

        #region ApplyBusinessRules
        // This is general purpose function where we will keep adding the business rules to accomdate 
        // deficiencies of ILP engine. Whenever the ILP engine is enhanced or the InstrumentImport made to work better
        // in Vantage, we need to revisit this area and do some cleanup.
        protected virtual void ApplyBusinessRules()
        {
            // Applix ID 45087 addressed here.
            DateTime curDatePlus480 = this.m_curDate.AddMonths(480);
            DateTime curDatePlus479 = this.m_curDate.AddMonths(479);

            ALCoreObj.Date compareDate480 = new ALCoreObj.Date(curDatePlus480.Year,curDatePlus480.Month,curDatePlus480.Day);
            ALCoreObj.Date compareDate479 = new ALCoreObj.Date(curDatePlus479.Year, curDatePlus479.Month, curDatePlus479.Day);

            switch (m_instrType)
            {
                #region Bullet type

                case ALConsts.InstrTypeBullet:// means At maturity same as Bullet type
                {
                    // Apply the maturity date issue BR refer 45017
                    // Apply the maturity date issue BR refer 37691
                    // Apply the maturity date issue BR refer 45095
                    if (m_instrBullet.mMatDate < m_instrBullet.mCurrDate)
                    {
                        m_instrBullet.mMatDate = m_instrBullet.mCurrDate;
                        m_instrBullet.mNextIntrPmtDate = m_instrBullet.mCurrDate;
                        m_instrBullet.mNextReprDate = m_instrBullet.mCurrDate;
                        m_instrBullet.mNextPrePmtDate = m_instrBullet.mCurrDate;
                    }
                    else if(m_instrBullet.mMatDate >= compareDate480)
                    {
                        // Applix ID 42049
                        // Applix ID 45186
                        m_instrBullet.mMatDate = compareDate479;
                    }

                    // 11/9/07 dkb - Applix 45189
                    while (m_instrBullet.mNextIntrPmtDate < m_instrBullet.mCurrDate)
                    {
                        m_instrBullet.mNextIntrPmtDate.AddMonths(m_instrBullet.mIntrPmtFreq);
                        if (m_instrBullet.mNextIntrPmtDate > m_instrBullet.mMatDate)
                        {
                            m_instrBullet.mNextIntrPmtDate = m_instrBullet.mMatDate;
                            break;
                        }
                    }

                    break;
                }
                #endregion

                #region Amortizing type

                case ALConsts.InstrTypeAmort:// means Amortized
                {
                    // Apply the maturity date issue BR refer 45017
                    // Apply the maturity date issue BR refer 45095
                    if (m_instrAmort.mMatDate < m_instrAmort.mCurrDate)
                    {
                        m_instrAmort.mMatDate = m_instrAmort.mCurrDate;
                        m_instrAmort.mNextPrinIntrPmtDate = m_instrAmort.mCurrDate;
                        m_instrAmort.mBalloonDate = m_instrAmort.mCurrDate;
                        m_instrAmort.mNextReprDate = m_instrAmort.mCurrDate;
                        m_instrAmort.mNextPrePmtDate = m_instrAmort.mCurrDate;
                    }
                    else if (m_instrAmort.mMatDate >= compareDate480)
                    {
                        // Applix ID 42049
                        // Applix ID 45186
                        m_instrAmort.mMatDate = compareDate479;
                    }

                    // Applix ID 45188 Valid only for spread evenly and amortizing types
                    // Applix ID 47454 KM 2/8/2008
                    // Applix ID 47452 KM 2/8/2008
                    if (m_instrAmort.mBalloonDate < m_instrAmort.mCurrDate)
                    {
                        m_instrAmort.mNextPrinIntrPmtDate = m_instrAmort.mCurrDate;
                        m_instrAmort.mBalloonDate = m_instrAmort.mCurrDate;
                        m_instrAmort.mNextReprDate = m_instrAmort.mCurrDate;
                        m_instrAmort.mNextPrePmtDate = m_instrAmort.mCurrDate;
                    }

                    // 11/9/07 dkb - Applix 45189
                    while (m_instrAmort.mNextPrinIntrPmtDate < m_instrAmort.mCurrDate)
                    {
                        m_instrAmort.mNextPrinIntrPmtDate.AddMonths(m_instrAmort.mPrinIntrPmtFreq);
                        if (m_instrAmort.mNextPrinIntrPmtDate > m_instrAmort.mMatDate)
                        {
                            m_instrAmort.mNextPrinIntrPmtDate = m_instrAmort.mMatDate;
                            break;
                        }
                    }
                    
                    break;
                }
                #endregion

                //#region Call Put type

                //case ALConsts.InstrTypeCallPut:// means Call type
                //{
                //    // Apply the maturity date issue BR refer 45017
                //    // Apply the maturity date issue BR refer 45095
                //    if (m_instrBullet.mMatDate < m_instrBullet.mCurrDate)
                //    {
                //        m_instrBullet.mMatDate = m_instrBullet.mCurrDate;
                //        m_instrBullet.mNextIntrPmtDate = m_instrBullet.mCurrDate;
                //        m_instrBullet.mNextReprDate = m_instrBullet.mCurrDate;
                //        m_instrBullet.mNextPrePmtDate = m_instrBullet.mCurrDate;
                //    }
                //    else if (m_instrBullet.mMatDate >= compareDate480)
                //    {
                //        // Applix ID 42049
                //        // Applix ID 45186
                //        m_instrBullet.mMatDate = compareDate479;
                //    }

                //    // 11/9/07 dkb - Applix 45189
                //    while (m_instrBullet.mNextIntrPmtDate < m_instrBullet.mCurrDate)
                //    {
                //        m_instrBullet.mNextIntrPmtDate.AddMonths(m_instrBullet.mIntrPmtFreq);
                //        if (m_instrBullet.mNextIntrPmtDate > m_instrBullet.mMatDate)
                //        {
                //            m_instrBullet.mNextIntrPmtDate = m_instrBullet.mMatDate;
                //            break;
                //        }
                //    }

                //    break;
                //}
                //#endregion

                #region SpreadEvenly type

                case ALConsts.InstrTypeSpreadEven:// means Spread Evenly type
                {
                    // Apply the maturity date issue BR refer 45017
                    // Apply the maturity date issue BR refer 45095
                    if (m_instrSpreadEvenly.mMatDate < m_instrSpreadEvenly.mCurrDate)
                    {
                        m_instrSpreadEvenly.mMatDate = m_instrSpreadEvenly.mCurrDate;
                        m_instrSpreadEvenly.mNextIntrPmtDate = m_instrSpreadEvenly.mCurrDate;
                        m_instrSpreadEvenly.mNextPrinPmtDate = m_instrSpreadEvenly.mCurrDate;
                        m_instrSpreadEvenly.mBalloonDate = m_instrSpreadEvenly.mCurrDate;
                        m_instrSpreadEvenly.mNextReprDate = m_instrSpreadEvenly.mCurrDate;
                        m_instrSpreadEvenly.mNextPrePmtDate = m_instrSpreadEvenly.mCurrDate;
                    }
                    else if (m_instrSpreadEvenly.mMatDate >= compareDate480)
                    {
                        // Applix ID 42049
                        // Applix ID 45186
                        m_instrSpreadEvenly.mMatDate = compareDate479;
                    }

                    // Applix ID 45188 Valid only for spread evenly and amortizing types
                    if (m_instrSpreadEvenly.mBalloonDate < m_instrSpreadEvenly.mCurrDate)
                    {
                        m_instrSpreadEvenly.mNextIntrPmtDate = m_instrSpreadEvenly.mCurrDate;
                        m_instrSpreadEvenly.mNextPrinPmtDate = m_instrSpreadEvenly.mCurrDate;
                        m_instrSpreadEvenly.mBalloonDate = m_instrSpreadEvenly.mCurrDate;
                        m_instrSpreadEvenly.mNextReprDate = m_instrSpreadEvenly.mCurrDate;
                        m_instrSpreadEvenly.mNextPrePmtDate = m_instrSpreadEvenly.mCurrDate;
                    }

                    // 11/9/07 dkb - Applix 45189
                    while (m_instrSpreadEvenly.mNextIntrPmtDate < m_instrSpreadEvenly.mCurrDate)
                    {
                        m_instrSpreadEvenly.mNextIntrPmtDate.AddMonths(m_instrSpreadEvenly.mIntrPmtFreq);
                        if (m_instrSpreadEvenly.mNextIntrPmtDate > m_instrSpreadEvenly.mMatDate)
                        {
                            m_instrSpreadEvenly.mNextIntrPmtDate = m_instrSpreadEvenly.mMatDate;
                            break;
                        }
                    }

                    // 11/9/07 dkb - Applix 45189
                    while (m_instrSpreadEvenly.mNextPrinPmtDate < m_instrSpreadEvenly.mCurrDate)
                    {
                        m_instrSpreadEvenly.mNextPrinPmtDate.AddMonths(m_instrSpreadEvenly.mIntrPmtFreq);
                        if (m_instrSpreadEvenly.mNextPrinPmtDate > m_instrSpreadEvenly.mMatDate)
                        {
                            m_instrSpreadEvenly.mNextPrinPmtDate = m_instrSpreadEvenly.mMatDate;
                            break;
                        }
                    }

                    break;
                }
                #endregion

                default:
                {
                    m_instrType = ALCoreObj.ALConsts.InstrTypeNone;
                    break;
                }
            }
        }
		#endregion

        #region GetCallPutFrequency
        // Part of Applix 46722 used by IncomeSimulation process in it's overridden call to ApplyBusinessRules
        protected int GetCallPutFrequency()
        {
            return m_bulletMapper.CallPutFrequency;
        }

        #endregion

        #region GetILPInstrumentType

        private int GetILPInstrumentType(int iType)
		{
			int temp;
			switch(iType) // 
			{
				case 1: // At Maturity
					temp =  ALConsts.InstrTypeBullet;
					break;
				case 2: // Amortized
					temp =  ALConsts.InstrTypeAmort;
					break;
                case 3:  // Call -- Applix # 38746
                    temp = ALConsts.InstrTypeBullet;
					break;
                case 4: // Put   -- Applix # 38746
                    temp = ALConsts.InstrTypeBullet;
					break;
				case 5: // Spread Evenly
					temp =  ALConsts.InstrTypeSpreadEven;
					break;
				default:
					temp =  ALConsts.InstrTypeNone;
					break;
			}
			return temp;

		}
		#endregion

		#region IDisposable Members

		public void Dispose()
		{
			// TODO:  Add InstrumentProcessor.Dispose implementation
			if( !m_bDisposed )
			{
				m_bDisposed = true;


				if( m_bulletMapper != null )
				{
					m_bulletMapper.InstrumentDataError -= new InstrDataErrorHandler( OnInstrumentError );
					m_bulletMapper.Dispose();
				}

				if( m_amortizedMapper != null )
				{
					m_amortizedMapper.InstrumentDataError -= new InstrDataErrorHandler( OnInstrumentError );
					m_amortizedMapper.Dispose();
				}

				if( m_spreadEvenlyMapper != null )
				{
					m_spreadEvenlyMapper.InstrumentDataError -= new InstrDataErrorHandler( OnInstrumentError );
					m_spreadEvenlyMapper.Dispose();
				}

				if( m_lookUpCache != null )
				{
					m_lookUpCache.Reset();
				}

				GC.SuppressFinalize(this);
			}		
		}

		#endregion
	}
}

